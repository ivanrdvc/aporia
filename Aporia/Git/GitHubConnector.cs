using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Aporia.Infra;
using Aporia.Infra.Cosmos;

namespace Aporia.Git;

public partial class GitHubConnector(
    IPrStateStore stateStore,
    HttpClient http,
    GitHubTokenService tokenService,
    IOptions<GitHubOptions> ghOptions,
    IOptions<AporiaOptions> aporiaOptions,
    ILogger<GitHubConnector> logger) : IGitConnector
{
    [GeneratedRegex(@"<!-- aporia:fp:(\w+) -->")]
    private static partial Regex FingerprintRegex();

    [GeneratedRegex(@"@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@")]
    private static partial Regex HunkHeaderRegex();

    [GeneratedRegex(@"\n\n```suggestion\n.*?\n```", RegexOptions.Singleline)]
    private static partial Regex SuggestionBlockRegex();
    private const string AporiaSummaryMarker = "<!-- aporia:summary -->";
    private static string AporiaReviewMarker => ChatRequest.ReviewMarker;
    private const string FingerprintPrefix = "<!-- aporia:fp:";
    private const int MaxFilesPerPage = 300;
    private const int MaxTotalFiles = 3000;
    private const int CompareFileCap = 250;
    private const int GitHubReviewBatchSize = 30;

    public async Task<ProjectConfig> GetConfig(ReviewRequest req)
    {
        var (owner, repo) = ParseRepoId(req.RepositoryId);
        var branch = req.TargetBranch;

        try
        {
            using var response = await SendAsync(req, HttpMethod.Get,
                $"repos/{owner}/{repo}/contents/.aporia.json?ref={branch}");
            if (!response.IsSuccessStatusCode)
                return ProjectConfig.Default;

            var content = await response.Content.ReadFromJsonAsync<GitHubContent>(JsonSerializerOptions.Web);
            if (content?.Content is null)
                return ProjectConfig.Default;

            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(content.Content.Replace("\n", "")));
            return ProjectConfig.Parse(raw);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch .aporia.json from GitHub");
            return ProjectConfig.Default;
        }
    }

    public async Task<Diff> GetDiff(ReviewRequest req, ProjectConfig config)
    {
        var (owner, repo) = ParseRepoId(req.RepositoryId);

        // Get PR to find head SHA
        var prResponse = await GetFromJsonAsync<GitHubPrResponse>(req,
            $"repos/{owner}/{repo}/pulls/{req.PullRequestId}");

        if (prResponse is null)
            return new Diff([]);

        var headSha = prResponse.Head.Sha;

        // Check incremental review state
        var incremental = aporiaOptions.Value.EnableIncrementalReviews;
        var state = await stateStore.GetAsync(req.RepositoryId, req.PullRequestId);
        var lastCursor = state?.Cursor;

        if (lastCursor is not null && !incremental)
            return new Diff([], headSha);

        if (lastCursor == headSha)
            return new Diff([], headSha);

        // Try incremental diff via compare API if we have a cursor
        List<GitHubPrFile>? incrementalFiles = null;
        if (incremental && lastCursor is not null)
        {
            try
            {
                using var compareResponse = await SendAsync(req, HttpMethod.Get,
                    $"repos/{owner}/{repo}/compare/{lastCursor}...{headSha}");

                if (compareResponse.IsSuccessStatusCode)
                {
                    var compare = await compareResponse.Content.ReadFromJsonAsync<GitHubCompareResponse>(JsonSerializerOptions.Web);
                    if (compare?.Files is { Count: 0 })
                        return new Diff([], headSha);
                    if (compare?.Files is { Count: > 0 and < CompareFileCap })
                        incrementalFiles = compare.Files;
                    // If >= CompareFileCap, result may be truncated — fall back to full diff
                }
                // 404 = force-push, cursor SHA gone — fall back to full diff
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Compare API failed for {Cursor}...{Head}, falling back to full diff", lastCursor, headSha);
            }
        }

        var prFiles = incrementalFiles ?? await GetAllPrFiles(req, owner, repo, req.PullRequestId);

        if (prFiles.Count > MaxTotalFiles)
        {
            logger.LogWarning("PR #{PrId} has {Count} files, capping at {Max}", req.PullRequestId, prFiles.Count, MaxTotalFiles);
            prFiles = prFiles[..MaxTotalFiles];
        }

        var bag = new ConcurrentBag<FileChange>();

        await Parallel.ForEachAsync(prFiles, new ParallelOptions { MaxDegreeOfParallelism = 10 },
            async (file, _) =>
            {
                var path = file.Filename;

                if (!config.Files.ShouldInclude(path))
                    return;

                var kind = file.Status switch
                {
                    "added" => ChangeKind.Add,
                    "removed" => ChangeKind.Delete,
                    "renamed" => ChangeKind.Rename,
                    _ => ChangeKind.Edit
                };

                if (kind == ChangeKind.Delete)
                {
                    bag.Add(new FileChange(path, ChangeKind.Delete, null));
                    return;
                }

                var patch = file.Patch;
                if (patch is null)
                {
                    logger.LogInformation("Skipping {Path} — GitHub returned no patch (binary or too large)", path);
                    return;
                }

                var content = await FetchFileContent(req, owner, repo, path, headSha);
                var oldPath = kind == ChangeKind.Rename ? file.PreviousFilename : null;

                bag.Add(new FileChange(path, kind, patch, content, oldPath));
            });

        return new Diff(bag.ToList(), headSha);
    }

    public async Task PostReview(ReviewRequest req, Diff diff, ReviewResult result)
    {
        var (owner, repo) = ParseRepoId(req.RepositoryId);
        var prNumber = req.PullRequestId;

        // Get head SHA for commit_id
        var headSha = diff.Cursor;

        // Dedup: collect existing aporia fingerprints
        var existingFingerprints = await GetExistingFingerprints(req, owner, repo, prNumber);

        var diffHunks = ParseDiffHunks(diff);
        var reviewComments = new List<object>();
        var outOfHunkFindings = new List<Finding>();

        foreach (var finding in result.Findings)
        {
            var fingerprint = Finding.Fingerprint(finding);
            if (existingFingerprints.Contains(fingerprint))
                continue;

            var path = finding.FilePath.TrimStart('/');
            var line = finding.EndLine ?? finding.StartLine;
            var startLine = finding.EndLine is not null && finding.StartLine != finding.EndLine
                ? finding.StartLine
                : (int?)null;

            if (!diffHunks.TryGetValue(path, out var hunks))
            {
                outOfHunkFindings.Add(finding);
                continue;
            }

            var hunk = hunks.FirstOrDefault(h => h.Contains(line));
            if (hunk is null)
            {
                outOfHunkFindings.Add(finding);
                continue;
            }

            // Clamp to hunk boundaries — GitHub accepts any line visible in the diff
            // (context + added), but rejects lines outside the hunk range.
            line = Math.Clamp(line, hunk.Start, hunk.End);
            if (startLine is not null)
            {
                startLine = Math.Clamp(startLine.Value, hunk.Start, hunk.End);
                if (startLine == line)
                    startLine = null;
            }

            var body = $"{FingerprintPrefix}{fingerprint} -->\n{finding.Message}";

            if (!string.IsNullOrWhiteSpace(finding.CodeFix))
                body += $"\n\n```suggestion\n{finding.CodeFix}\n```";

            var comment = new Dictionary<string, object>
            {
                ["path"] = path,
                ["line"] = line,
                ["side"] = "RIGHT",
                ["body"] = body
            };

            if (startLine is not null)
            {
                comment["start_line"] = startLine.Value;
                comment["start_side"] = "RIGHT";
            }

            reviewComments.Add(comment);
        }

        // Post or update summary first so it appears above inline comments on the PR timeline
        if (!string.IsNullOrWhiteSpace(result.Summary))
            await UpsertSummaryComment(req, owner, repo, prNumber, result.Summary);

        // Post review with findings
        if (reviewComments.Count > 0 || outOfHunkFindings.Count > 0)
        {
            var reviewBody = AporiaReviewMarker;
            if (outOfHunkFindings.Count > 0)
            {
                reviewBody += "\n\n**Additional findings (outside diff hunks):**\n";
                foreach (var f in outOfHunkFindings)
                    reviewBody += $"- `{f.FilePath}:{f.StartLine}` — {f.Message}\n";
            }

            // Batch into groups of GitHubReviewBatchSize
            var batches = reviewComments.Chunk(GitHubReviewBatchSize).ToList();
            for (var i = 0; i < Math.Max(batches.Count, 1); i++)
            {
                var batch = i < batches.Count ? batches[i] : [];
                var payload = new Dictionary<string, object>
                {
                    ["event"] = "COMMENT",
                    ["body"] = i == 0 ? reviewBody : AporiaReviewMarker,
                    ["comments"] = batch
                };

                if (headSha is not null)
                    payload["commit_id"] = headSha;

                using var response = await SendAsync(req, HttpMethod.Post,
                    $"repos/{owner}/{repo}/pulls/{prNumber}/reviews", payload);

                if (response.StatusCode == HttpStatusCode.UnprocessableEntity && batch.Length > 0)
                    await RetryCommentsIndividually(req, owner, repo, prNumber, headSha, batch);
            }
        }

        // Save cursor
        if (diff.Cursor is not null)
            await stateStore.SaveAsync(req.RepositoryId, req.PullRequestId, diff.Cursor);
    }

    public async Task<string?> GetFile(ReviewRequest req, string path)
    {
        var (owner, repo) = ParseRepoId(req.RepositoryId);
        return await FetchFileContent(req, owner, repo, path, req.SourceBranch);
    }

    public async Task<IReadOnlyList<string>> ListFiles(ReviewRequest req, string path, bool recursive = false)
    {
        var (owner, repo) = ParseRepoId(req.RepositoryId);
        var branch = req.SourceBranch;

        if (recursive)
        {
            var response = await GetFromJsonAsync<GitHubTreeResponse>(req,
                $"repos/{owner}/{repo}/git/trees/{branch}?recursive=1");

            return response?.Tree
                ?.Where(t => t.Type == "blob" && t.Path.StartsWith(path.TrimStart('/')))
                .Select(t => t.Path)
                .ToList() ?? [];
        }

        var contents = await GetFromJsonAsync<List<GitHubContent>>(req,
            $"repos/{owner}/{repo}/contents/{path.TrimStart('/')}?ref={branch}");

        return contents?.Select(c => c.Path).ToList() ?? [];
    }

    public async Task<IReadOnlyList<SearchResult>> SearchCode(ReviewRequest req, string query)
    {
        var (owner, repo) = ParseRepoId(req.RepositoryId);

        using var response = await SendAsync(req, HttpMethod.Get,
            $"search/code?q={Uri.EscapeDataString(query)}+repo:{owner}/{repo}");

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            logger.LogWarning("GitHub code search rate limited for {Repo}", req.RepositoryId);
            return [new SearchResult("", 0, "Search unavailable (rate limited)")];
        }

        if (!response.IsSuccessStatusCode)
            return [];

        var result = await response.Content.ReadFromJsonAsync<GitHubSearchResponse>(JsonSerializerOptions.Web);

        return result?.Items
            ?.Select(i => new SearchResult(i.Path, 0, i.Name))
            .ToList() ?? [];
    }

    public async Task<PrContext> GetPrContext(ReviewRequest req)
    {
        var (owner, repo) = ParseRepoId(req.RepositoryId);

        try
        {
            var prTask = GetFromJsonAsync<GitHubPrResponse>(req,
                $"repos/{owner}/{repo}/pulls/{req.PullRequestId}");
            var commitsTask = GetFromJsonAsync<List<GitHubCommit>>(req,
                $"repos/{owner}/{repo}/pulls/{req.PullRequestId}/commits");

            await Task.WhenAll(prTask, commitsTask);

            var pr = prTask.Result;
            var messages = commitsTask.Result?
                .Select(c => c.Commit.Message)
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .ToList() ?? [];

            return new PrContext(pr?.Title ?? "", pr?.Body, messages);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch PR context for PR #{PrId}", req.PullRequestId);
            return new PrContext("", null, []);
        }
    }

    public Task<ChatThreadContext?> GetChatThreadContext(ReviewRequest req, int threadId, int commentId)
        => Task.FromResult<ChatThreadContext?>(null);

    public Task PostChatReply(ReviewRequest req, int threadId, string body)
        => Task.CompletedTask;

    private Task<HttpResponseMessage> SendAsync(ReviewRequest req, HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        if (req.InstallationId is { } id)
            request.Options.Set(GitHubAuthHandler.InstallationIdKey, id);
        return http.SendAsync(request);
    }

    private async Task<T?> GetFromJsonAsync<T>(ReviewRequest req, string url)
    {
        using var response = await SendAsync(req, HttpMethod.Get, url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonSerializerOptions.Web);
    }

    private static (string Owner, string Repo) ParseRepoId(string repositoryId)
    {
        var parts = repositoryId.Split('/');
        return (parts[0], parts[1]);
    }

    private async Task<List<GitHubPrFile>> GetAllPrFiles(ReviewRequest req, string owner, string repo, int prNumber)
    {
        var allFiles = new List<GitHubPrFile>();

        await foreach (var file in Paginate<GitHubPrFile>(req,
            $"repos/{owner}/{repo}/pulls/{prNumber}/files", MaxFilesPerPage))
        {
            allFiles.Add(file);
            if (allFiles.Count >= MaxTotalFiles)
                break;
        }

        return allFiles;
    }

    private async Task<string?> FetchFileContent(ReviewRequest req, string owner, string repo, string path, string @ref)
    {
        try
        {
            using var response = await SendAsync(req, HttpMethod.Get,
                $"repos/{owner}/{repo}/contents/{path.TrimStart('/')}?ref={@ref}");

            if (!response.IsSuccessStatusCode)
                return null;

            var content = await response.Content.ReadFromJsonAsync<GitHubContent>(JsonSerializerOptions.Web);

            if (content is null) return null;

            // Files > 1MB need the blob API
            if (content.Content is null && content.GitUrl is not null)
            {
                var blobResponse = await GetFromJsonAsync<GitHubBlob>(req, content.GitUrl);
                if (blobResponse?.Content is null) return null;
                return Encoding.UTF8.GetString(Convert.FromBase64String(blobResponse.Content.Replace("\n", "")));
            }

            if (content.Content is null) return null;
            return Encoding.UTF8.GetString(Convert.FromBase64String(content.Content.Replace("\n", "")));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch file {Path} from GitHub", path);
            return null;
        }
    }

    /// <summary>
    /// Collect existing aporia fingerprints from review comments for dedup.
    /// </summary>
    private async Task<HashSet<string>> GetExistingFingerprints(
        ReviewRequest req, string owner, string repo, int prNumber)
    {
        var fingerprints = new HashSet<string>();

        try
        {
            await foreach (var comment in Paginate<GitHubReviewComment>(req,
                $"repos/{owner}/{repo}/pulls/{prNumber}/comments"))
            {
                var match = FingerprintRegex().Match(comment.Body ?? "");
                if (match.Success)
                    fingerprints.Add(match.Groups[1].Value);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch review comments for PR #{PrId}", prNumber);
        }

        return fingerprints;
    }

    private async Task UpsertSummaryComment(ReviewRequest req, string owner, string repo, int prNumber, string summary)
    {
        var body = $"{AporiaSummaryMarker}\n{summary}";

        await foreach (var comment in Paginate<GitHubIssueComment>(req,
            $"repos/{owner}/{repo}/issues/{prNumber}/comments"))
        {
            if (comment.Body?.Contains(AporiaSummaryMarker) == true)
            {
                using var _ = await SendAsync(req, HttpMethod.Patch,
                    $"repos/{owner}/{repo}/issues/comments/{comment.Id}",
                    new { body });
                return;
            }
        }

        (await SendAsync(req, HttpMethod.Post,
            $"repos/{owner}/{repo}/issues/{prNumber}/comments",
            new { body })).Dispose();
    }

    private async IAsyncEnumerable<T> Paginate<T>(ReviewRequest req, string url, int perPage = 100)
    {
        var page = 1;

        while (true)
        {
            var separator = url.Contains('?') ? '&' : '?';
            using var response = await SendAsync(req, HttpMethod.Get,
                $"{url}{separator}per_page={perPage}&page={page}");

            if (!response.IsSuccessStatusCode) yield break;

            var items = await response.Content.ReadFromJsonAsync<List<T>>(JsonSerializerOptions.Web);
            if (items is null or { Count: 0 }) yield break;

            foreach (var item in items)
                yield return item;

            if (items.Count < perPage) yield break;
            page++;
        }
    }

    private async Task RetryCommentsIndividually(
        ReviewRequest req, string owner, string repo, int prNumber, string? headSha, object[] comments)
    {
        foreach (var comment in comments)
        {
            if (comment is not Dictionary<string, object> original)
                continue;

            // Work on a copy so we don't mutate the caller's data
            var dict = new Dictionary<string, object>(original);

            var payload = new Dictionary<string, object>
            {
                ["event"] = "COMMENT",
                ["body"] = AporiaReviewMarker,
                ["comments"] = new[] { dict }
            };

            if (headSha is not null)
                payload["commit_id"] = headSha;

            var response = await SendAsync(req, HttpMethod.Post,
                $"repos/{owner}/{repo}/pulls/{prNumber}/reviews", payload);

            if (response.StatusCode != HttpStatusCode.UnprocessableEntity)
                continue;

            // Retry without suggestion block
            if (dict.TryGetValue("body", out var bodyObj) && bodyObj is string bodyStr)
            {
                var stripped = SuggestionBlockRegex().Replace(bodyStr, "");
                if (stripped != bodyStr)
                {
                    dict["body"] = stripped;
                    response = await SendAsync(req, HttpMethod.Post,
                        $"repos/{owner}/{repo}/pulls/{prNumber}/reviews", payload);

                    if (response.StatusCode != HttpStatusCode.UnprocessableEntity)
                        continue;
                }

                // Retry as single-line (drop start_line/start_side)
                dict.Remove("start_line");
                dict.Remove("start_side");
                response = await SendAsync(req, HttpMethod.Post,
                    $"repos/{owner}/{repo}/pulls/{prNumber}/reviews", payload);

                if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
                    logger.LogWarning("Skipping comment on {Path}:{Line} — GitHub rejected it",
                        dict.GetValueOrDefault("path"), dict.GetValueOrDefault("line"));
            }
        }
    }

    private static Dictionary<string, List<DiffHunk>> ParseDiffHunks(Diff diff)
    {
        var hunks = new Dictionary<string, List<DiffHunk>>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in diff.Files)
        {
            if (file.Patch is null) continue;

            var path = file.Path.TrimStart('/');
            var fileHunks = new List<DiffHunk>();

            foreach (Match match in HunkHeaderRegex().Matches(file.Patch))
            {
                var start = int.Parse(match.Groups[1].Value);
                var count = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 1;
                var end = start + count - 1;

                fileHunks.Add(new DiffHunk(start, end));
            }

            hunks[path] = fileHunks;
        }

        return hunks;
    }

    private record DiffHunk(int Start, int End)
    {
        public bool Contains(int line) => line >= Start && line <= End;
    }

    public async Task<CloneCredentials> GetCloneCredentials(ReviewRequest req)
    {
        var config = ghOptions.Value;
        var token = config.UseApp && req.InstallationId is { } id
            ? await tokenService.GetInstallationTokenAsync(config, id)
            : config.Token ?? throw new InvalidOperationException("No GitHub auth configured for cloning.");

        return new($"https://github.com/{req.Organization}/{req.RepositoryName}.git", token);
    }
}
