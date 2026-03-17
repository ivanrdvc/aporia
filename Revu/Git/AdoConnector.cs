using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;

using Revu.Infra;
using Revu.Infra.Cosmos;

namespace Revu.Git;

public class AdoConnector(
    IPrStateStore stateStore,
    IOptions<AdoOptions> adoOptions,
    IOptions<RevuOptions> revuOptions,
    ILogger<AdoConnector> logger) : IGitConnector
{
    private const string RevuVersion = "revu:version";
    private const string RevuFingerprint = "revu:fingerprint";
    private static string RevuReviewMarker => ChatRequest.ReviewMarker;
    private const int MaxChangeEntries = 5000;

    internal readonly ConcurrentDictionary<string, GitHttpClient> _gitClients = [];
    internal readonly ConcurrentDictionary<string, HttpClient> _searchClients = [];
    private readonly ConcurrentDictionary<string, VssConnection> _connections = [];

    private VssConnection GetConnection(string org) =>
        _connections.GetOrAdd(org, key =>
        {
            var config = adoOptions.Value.Organizations[key];
            return new VssConnection(
                new Uri($"https://dev.azure.com/{config.Organization}"),
                new VssBasicCredential(string.Empty, config.PersonalAccessToken));
        });

    private GitHttpClient GetGitClient(string org) =>
        _gitClients.GetOrAdd(org, key => GetConnection(key).GetClient<GitHttpClient>());

    private WorkItemTrackingHttpClient GetWitClient(string org) =>
        GetConnection(org).GetClient<WorkItemTrackingHttpClient>();

    private HttpClient GetSearchClient(string org) =>
        _searchClients.GetOrAdd(org, key =>
        {
            var config = adoOptions.Value.Organizations[key];
            var client = new HttpClient
            {
                BaseAddress = new Uri($"https://almsearch.dev.azure.com/{config.Organization}/")
            };
            var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($":{config.PersonalAccessToken}"));
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", creds);
            return client;
        });

    public async Task<ProjectConfig> GetConfig(ReviewRequest req)
    {
        var git = GetGitClient(req.Organization);
        try
        {
            var item = await git.GetItemAsync(
                project: req.Project,
                repositoryId: req.RepositoryId,
                path: ".revu.json",
                includeContent: true,
                versionDescriptor: new GitVersionDescriptor
                {
                    Version = req.TargetBranch.Replace("refs/heads/", ""),
                    VersionType = GitVersionType.Branch
                });

            return item?.Content is not null
                ? ProjectConfig.Parse(item.Content)
                : ProjectConfig.Default;
        }
        catch (VssServiceException)
        {
            return ProjectConfig.Default;
        }
    }

    public async Task<Diff> GetDiff(ReviewRequest req, ProjectConfig config)
    {
        var git = GetGitClient(req.Organization);

        var iterations = await git.GetPullRequestIterationsAsync(
            project: req.Project,
            repositoryId: req.RepositoryId,
            pullRequestId: req.PullRequestId);

        if (iterations.Count == 0)
            return new Diff([]);

        var lastIteration = iterations[^1];
        if (lastIteration.Id is not { } iterationId)
            return new Diff([]);

        var incremental = revuOptions.Value.EnableIncrementalReviews;
        var state = await stateStore.GetAsync(req.RepositoryId, req.PullRequestId);
        var lastReviewedIteration = state is not null ? int.Parse(state.Cursor) : (int?)null;

        // Already reviewed — skip if non-incremental (review once only) or same iteration
        if (lastReviewedIteration is not null && (!incremental || lastReviewedIteration >= iterationId))
            return new Diff([], iterationId.ToString());

        // Paginate to get changed files — compareTo narrows to new changes when incremental
        var allChanges = new List<GitPullRequestChange>();
        int top = 2000;
        int skip = 0;
        while (top > 0)
        {
            var page = await git.GetPullRequestIterationChangesAsync(
                project: req.Project,
                repositoryId: req.RepositoryId,
                pullRequestId: req.PullRequestId,
                iterationId: iterationId,
                top: top,
                skip: skip,
                compareTo: incremental ? lastReviewedIteration : null);

            allChanges.AddRange(page.ChangeEntries ?? []);
            top = page.NextTop;
            skip = page.NextSkip;
        }

        if (allChanges.Count > MaxChangeEntries)
            allChanges = allChanges[..MaxChangeEntries];

        // Pin to iteration commit SHAs to avoid race conditions with branch tip movement
        var sourceCommit = lastIteration.SourceRefCommit.CommitId;
        var targetCommit = lastIteration.TargetRefCommit.CommitId;
        var bag = new ConcurrentBag<FileChange>();

        await Parallel.ForEachAsync(allChanges, new ParallelOptions { MaxDegreeOfParallelism = 10 },
            async (change, _) =>
            {
                var path = change.Item.Path;

                if (change.ChangeType.HasFlag(VersionControlChangeType.Delete))
                {
                    if (config.Files.ShouldInclude(path))
                        bag.Add(new FileChange(path, ChangeKind.Delete, null));
                    return;
                }

                if (change.ChangeType.HasFlag(VersionControlChangeType.Rename))
                {
                    if (!config.Files.ShouldInclude(path)) return;

                    var oldPath = change.OriginalPath;
                    var sourceContent = await ReadFile(req, path, sourceCommit);
                    if (sourceContent is null) return;

                    string? diffContent = null;
                    if (change.ChangeType.HasFlag(VersionControlChangeType.Edit))
                    {
                        var baseContent = await ReadFile(req, oldPath ?? path, targetCommit) ?? string.Empty;
                        diffContent = DiffBuilder.Hunks(baseContent, sourceContent);
                    }

                    bag.Add(new FileChange(path, ChangeKind.Rename, diffContent, sourceContent, oldPath));
                    return;
                }

                if (change.ChangeType is not (VersionControlChangeType.Add or VersionControlChangeType.Edit))
                    return;

                if (!config.Files.ShouldInclude(path)) return;

                if (change.ChangeType == VersionControlChangeType.Add)
                {
                    var content = await ReadFile(req, path, sourceCommit);
                    if (content is null) return;
                    bag.Add(new FileChange(path, ChangeKind.Add, DiffBuilder.NewFile(content), content));
                }
                else
                {
                    var sourceTask = ReadFile(req, path, sourceCommit);
                    var baseTask = ReadFile(req, path, targetCommit);
                    await Task.WhenAll(sourceTask, baseTask);

                    var content = sourceTask.Result;
                    if (content is null) return;
                    var baseContent = baseTask.Result ?? string.Empty;
                    bag.Add(new FileChange(path, ChangeKind.Edit, DiffBuilder.Hunks(baseContent, content), content));
                }
            });

        var files = bag.ToList();

        return new Diff(files, iterationId.ToString());
    }

    public async Task PostReview(ReviewRequest req, Diff diff, ReviewResult result)
    {
        var git = GetGitClient(req.Organization);

        // Fetch existing Revu threads for dedup
        var existingThreads = await git.GetThreadsAsync(
            project: req.Project,
            repositoryId: req.RepositoryId,
            pullRequestId: req.PullRequestId);

        var revuThreads = existingThreads
            .Where(t => !t.IsDeleted)
            .Where(t => t.Properties?.GetValue<string>(RevuVersion, null!) is not null)
            .Where(t => !string.IsNullOrEmpty(t.Properties!.GetValue<string>(RevuFingerprint, "")))
            .GroupBy(t => t.Properties!.GetValue<string>(RevuFingerprint, ""))
            .ToDictionary(g => g.Key, g => g.First());

        // Post new findings, skipping those already posted (fingerprint match)
        foreach (var finding in result.Findings)
        {
            var fingerprint = Finding.Fingerprint(finding);

            if (revuThreads.ContainsKey(fingerprint))
                continue; // already posted in a prior review

            var thread = new GitPullRequestCommentThread
            {
                Comments = [new Comment { Content = $"{RevuReviewMarker}\n{FormatComment(finding)}", CommentType = CommentType.Text }],
                Status = CommentThreadStatus.Active,
                Properties = new PropertiesCollection
                {
                    { RevuVersion, "1" },
                    { RevuFingerprint, fingerprint }
                },
                ThreadContext = new CommentThreadContext
                {
                    FilePath = "/" + finding.FilePath.TrimStart('/'),
                    RightFileStart = new CommentPosition { Line = finding.StartLine, Offset = 1 },
                    RightFileEnd = new CommentPosition
                    {
                        Line = finding.EndLine ?? finding.StartLine,
                        Offset = int.MaxValue
                    }
                }
            };

            await git.CreateThreadAsync(thread, req.Project, req.RepositoryId, req.PullRequestId);
        }

        if (!string.IsNullOrWhiteSpace(result.Summary))
        {
            var summaryThread = new GitPullRequestCommentThread
            {
                Comments = [new Comment { Content = $"{RevuReviewMarker}\n{result.Summary}", CommentType = CommentType.Text }],
                Properties = new PropertiesCollection { { RevuVersion, "1" } },
                Status = CommentThreadStatus.Closed
            };

            await git.CreateThreadAsync(summaryThread, req.Project, req.RepositoryId, req.PullRequestId);
        }

        // Save cursor state after successful posting
        if (diff.Cursor is not null)
            await stateStore.SaveAsync(req.RepositoryId, req.PullRequestId, diff.Cursor);
    }

    public async Task<string?> GetFile(ReviewRequest req, string path)
    {
        try
        {
            var git = GetGitClient(req.Organization);
            var item = await git.GetItemAsync(
                project: req.Project,
                repositoryId: req.RepositoryId,
                path: path,
                includeContent: true,
                versionDescriptor: new GitVersionDescriptor
                {
                    Version = req.SourceBranch.Replace("refs/heads/", ""),
                    VersionType = GitVersionType.Branch
                });

            return item?.IsFolder == true ? null : item?.Content;
        }
        catch (VssServiceException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<string>> ListFiles(ReviewRequest req, string path, bool recursive = false)
    {
        var client = GetGitClient(req.Organization);
        var items = await client.GetItemsAsync(
            project: req.Project,
            repositoryId: req.RepositoryId,
            scopePath: path,
            recursionLevel: recursive ? VersionControlRecursionType.Full : VersionControlRecursionType.OneLevel,
            versionDescriptor: new GitVersionDescriptor
            {
                Version = req.SourceBranch.Replace("refs/heads/", ""),
                VersionType = GitVersionType.Branch
            });

        if (recursive)
            return items?.Where(i => !i.IsFolder).Select(i => i.Path).ToList() ?? [];

        return items?.Select(i => i.Path).Where(p => p != path).ToList() ?? [];
    }

    public async Task<IReadOnlyList<SearchResult>> SearchCode(ReviewRequest req, string query)
    {
        var body = new Dictionary<string, object>
        {
            ["searchText"] = query,
            ["$top"] = 10,
            ["includeSnippet"] = true,
            ["filters"] = new Dictionary<string, string[]>
            {
                ["Project"] = [req.Project],
                ["Repository"] = [req.RepositoryName]
            }
        };

        var search = GetSearchClient(req.Organization);
        var response = await search.PostAsJsonAsync(
            $"{req.Project}/_apis/search/codesearchresults?api-version=7.1", body);

        if (!response.IsSuccessStatusCode)
            return [];

        var result = await response.Content.ReadFromJsonAsync<CodeSearchResponse>(JsonSerializerOptions.Web);
        return result?.Results?
            .Select(r =>
            {
                var matchCount = r.Matches?.GetValueOrDefault("content")?.Count ?? 0;
                var snippet = matchCount > 0 ? $"{matchCount} match(es)" : r.FileName;
                return new SearchResult(r.Path, 0, snippet);
            })
            .ToList() ?? [];
    }

    public async Task<PrContext> GetPrContext(ReviewRequest req)
    {
        var git = GetGitClient(req.Organization);
        try
        {
            var prTask = git.GetPullRequestByIdAsync(req.PullRequestId, req.Project);
            var commitsTask = git.GetPullRequestCommitsAsync(req.Project, req.RepositoryId, req.PullRequestId);
            var workItemsTask = revuOptions.Value.EnableWorkItems
                ? FetchWorkItems(req)
                : Task.FromResult<IReadOnlyList<WorkItemContext>?>(null);

            await Task.WhenAll(prTask, commitsTask, workItemsTask);

            var pr = prTask.Result;
            var messages = commitsTask.Result
                .Select(c => c.Comment)
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .ToList();

            return new PrContext(pr.Title, pr.Description, messages, workItemsTask.Result);
        }
        catch (VssServiceException ex)
        {
            logger.LogWarning(ex, "Failed to fetch PR context for PR #{PrId}", req.PullRequestId);
            return new PrContext("", null, []);
        }
    }

    private async Task<IReadOnlyList<WorkItemContext>?> FetchWorkItems(ReviewRequest req)
    {
        try
        {
            var git = GetGitClient(req.Organization);
            var wit = GetWitClient(req.Organization);

            var refs = await git.GetPullRequestWorkItemRefsAsync(req.Project, req.RepositoryId, req.PullRequestId);
            if (refs.Count == 0)
                return null;

            var ids = refs
                .Select(r => int.TryParse(r.Url.Split('/').Last(), out var id) ? id : (int?)null)
                .Where(id => id is not null)
                .Select(id => id!.Value)
                .ToList();

            if (ids.Count == 0)
                return null;

            var workItems = await wit.GetWorkItemsAsync(ids, fields: AdoWorkItemMapper.Fields);

            // Collect distinct parent IDs and batch-fetch them
            var parentIds = workItems
                .Where(wi => wi.Fields is not null)
                .Select(AdoWorkItemMapper.GetParentId)
                .Where(id => id is not null)
                .Select(id => id!.Value)
                .Distinct()
                .ToList();

            var parentMap = new Dictionary<int, WorkItemContext>();
            if (parentIds.Count > 0)
            {
                var parentWorkItems = await wit.GetWorkItemsAsync(parentIds, fields: AdoWorkItemMapper.Fields);
                foreach (var pwi in parentWorkItems)
                {
                    if (pwi.Id is not null && pwi.Fields is not null)
                        parentMap[pwi.Id.Value] = AdoWorkItemMapper.ToContext(pwi);
                }
            }

            var items = AdoWorkItemMapper.MapWithParents(workItems, parentMap);
            return items.Count > 0 ? items : null;
        }
        catch (VssServiceException ex)
        {
            logger.LogWarning(ex, "Failed to fetch work items for PR #{PrId}", req.PullRequestId);
            return null;
        }
    }

    public async Task<ChatThreadContext?> GetChatThreadContext(ReviewRequest req, int threadId, int commentId)
    {
        var git = GetGitClient(req.Organization);
        var threads = await git.GetThreadsAsync(
            project: req.Project,
            repositoryId: req.RepositoryId,
            pullRequestId: req.PullRequestId);

        var thread = threads.FirstOrDefault(t => t.Id == threadId);
        if (thread is null || thread.IsDeleted || thread.Comments is null)
            return null;

        var comment = thread.Comments.FirstOrDefault(c => c.Id == commentId);
        if (comment?.Content is null)
            return null;

        // Ignore Revu's own comments (review findings, summary, chat replies)
        if (comment.Content.StartsWith(ChatRequest.MarkerPrefix))
            return null;

        var isRevuThread = thread.Properties?.GetValue<string>(RevuVersion, null!) is not null;

        // On non-Revu threads, only respond if explicitly mentioned
        if (!isRevuThread && !comment.Content.Contains("@revu", StringComparison.OrdinalIgnoreCase))
            return null;

        var fingerprint = thread.Properties?.GetValue<string>(RevuFingerprint, null!);
        var filePath = thread.ThreadContext?.FilePath;
        var startLine = thread.ThreadContext?.RightFileStart?.Line;

        var messages = thread.Comments
            .Where(c => !c.IsDeleted && c.CommentType != CommentType.System)
            .OrderBy(c => c.PublishedDate)
            .Select(c => c.Content ?? "")
            .Where(c => c.Length > 0)
            .ToList();

        return new ChatThreadContext(thread.Id, fingerprint, filePath, startLine, messages);
    }

    public async Task PostChatReply(ReviewRequest req, int threadId, string body)
    {
        var git = GetGitClient(req.Organization);

        var comment = new Comment
        {
            Content = $"{ChatRequest.ChatMarker}\n{body}",
            CommentType = CommentType.Text
        };

        await git.CreateCommentAsync(comment, req.Project, req.RepositoryId, req.PullRequestId, threadId);
    }

    private async Task<string?> ReadFile(
        ReviewRequest req,
        string path,
        string version,
        GitVersionType versionType = GitVersionType.Commit)
    {
        try
        {
            var stream = await GetGitClient(req.Organization).GetItemContentAsync(
                project: req.Project,
                repositoryId: req.RepositoryId,
                path: path,
                versionDescriptor: new GitVersionDescriptor
                {
                    Version = version,
                    VersionType = versionType
                });
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync();
        }
        catch (VssServiceException)
        {
            return null;
        }
    }

    private record CodeSearchResponse(int Count, List<CodeSearchHit>? Results);
    private record CodeSearchHit(string FileName, string Path, Dictionary<string, List<CodeSearchMatch>?>? Matches = null);
    private record CodeSearchMatch(int CharOffset, int Length);

    private static string FormatComment(Finding finding)
    {
        if (string.IsNullOrWhiteSpace(finding.CodeFix))
            return finding.Message;

        return $"{finding.Message}\n\n```suggestion\n{finding.CodeFix}\n```";
    }
}
