using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.Options;

using Aporia.Git;

using Xunit.Abstractions;

namespace Aporia.Tests.Integration.Fixtures;

internal class GitHubTestHelper : ITestHelper, IDisposable
{
    private readonly TestRepoOptions _options;
    private readonly HttpClient _client;

    public GitHubTestHelper(IOptions<TestRepoOptions> options, IOptions<GitHubOptions> ghOptions)
    {
        _options = options.Value;
        var token = ghOptions.Value.Token
            ?? throw new InvalidOperationException("GitHub__Token must be set for integration tests.");

        _client = new HttpClient { BaseAddress = new Uri("https://api.github.com/") };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Aporia", "1.0"));
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public ReviewRequest BuildRequest(int prId, string sourceBranch, string targetBranch = "refs/heads/main") =>
        new(_options.Provider, _options.Project, _options.RepositoryId, _options.RepositoryName,
            prId, sourceBranch, targetBranch, _options.Organization ?? "",
            InstallationId: _options.InstallationId);

    public async Task<int> GetAporiaCommentCount(ReviewRequest req)
    {
        var (owner, repo) = ParseRepoId(req.RepositoryId);
        var comments = await _client.GetFromJsonAsync<List<ReviewComment>>(
            $"repos/{owner}/{repo}/pulls/{req.PullRequestId}/comments?per_page=100", JsonSerializerOptions.Web);

        return comments?.Count(c => c.Body?.Contains("<!-- aporia:fp:") == true) ?? 0;
    }

    public async Task PrintComments(ReviewRequest req, ITestOutputHelper output)
    {
        var (owner, repo) = ParseRepoId(req.RepositoryId);

        var reviews = await _client.GetFromJsonAsync<List<Review>>(
            $"repos/{owner}/{repo}/pulls/{req.PullRequestId}/reviews", JsonSerializerOptions.Web);

        var comments = await _client.GetFromJsonAsync<List<ReviewComment>>(
            $"repos/{owner}/{repo}/pulls/{req.PullRequestId}/comments?per_page=100", JsonSerializerOptions.Web);

        output.WriteLine($"=== PR reviews ({reviews?.Count ?? 0}), comments ({comments?.Count ?? 0}) ===\n");

        if (reviews is not null)
        {
            foreach (var review in reviews)
            {
                output.WriteLine($"--- Review #{review.Id} [{review.State}] ---");
                if (!string.IsNullOrWhiteSpace(review.Body))
                    output.WriteLine(review.Body);
                output.WriteLine("");
            }
        }

        if (comments is not null)
        {
            foreach (var comment in comments)
            {
                output.WriteLine($"--- {comment.Path}:{comment.Line} ---");
                output.WriteLine(comment.Body ?? "(empty)");
                output.WriteLine("");
            }
        }
    }

    public async Task<(long ThreadId, long CommentId)> PostCommentOnAporiaThread(ReviewRequest req, string message)
    {
        var (owner, repo) = ParseRepoId(req.RepositoryId);
        var comments = await _client.GetFromJsonAsync<List<ReviewComment>>(
            $"repos/{owner}/{repo}/pulls/{req.PullRequestId}/comments?per_page=100", JsonSerializerOptions.Web);

        var root = comments?.FirstOrDefault(c => c.Body?.Contains("<!-- aporia:fp:") == true && c.InReplyToId is null)
                   ?? throw new InvalidOperationException("No Aporia review comment found to reply to");

        var reply = await _client.PostAsJsonAsync(
            $"repos/{owner}/{repo}/pulls/{req.PullRequestId}/comments/{root.Id}/replies",
            new { body = message });
        reply.EnsureSuccessStatusCode();

        var posted = await reply.Content.ReadFromJsonAsync<ReviewComment>(JsonSerializerOptions.Web);
        return (root.Id, posted!.Id);
    }

    public async Task<(long ThreadId, long CommentId, string Message)> FindLatestHumanComment(ReviewRequest req)
    {
        var (owner, repo) = ParseRepoId(req.RepositoryId);
        var comments = await _client.GetFromJsonAsync<List<ReviewComment>>(
            $"repos/{owner}/{repo}/pulls/{req.PullRequestId}/comments?per_page=100", JsonSerializerOptions.Web);

        // Find human replies (non-marker) on Aporia threads
        var aporiaRootIds = comments?
            .Where(c => c.Body?.Contains("<!-- aporia:fp:") == true && c.InReplyToId is null)
            .Select(c => c.Id)
            .ToHashSet() ?? [];

        var human = comments?
            .Where(c => c.InReplyToId is not null && aporiaRootIds.Contains(c.InReplyToId.Value))
            .Where(c => c.Body is not null && !c.Body.StartsWith(ChatRequest.MarkerPrefix))
            .OrderByDescending(c => c.Id)
            .FirstOrDefault();

        if (human is not null)
            return (human.InReplyToId!.Value, human.Id, human.Body!);

        throw new InvalidOperationException("No human comment found on any Aporia thread");
    }

    private static (string Owner, string Repo) ParseRepoId(string repositoryId)
    {
        var parts = repositoryId.Split("__");
        return (parts[0], parts[1]);
    }

    public void Dispose() => _client.Dispose();

    private record Review(long Id, string? Body, string? State);
    private record ReviewComment(
        long Id,
        string? Body,
        string? Path,
        int? Line,
        [property: System.Text.Json.Serialization.JsonPropertyName("in_reply_to_id")] long? InReplyToId = null);
}
