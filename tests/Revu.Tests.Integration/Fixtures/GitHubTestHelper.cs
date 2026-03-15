using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Options;

using Revu.Git;

using Xunit.Abstractions;

namespace Revu.Tests.Integration.Fixtures;

internal class GitHubTestHelper : ITestHelper, IDisposable
{
    private readonly TestRepoOptions _options;
    private readonly HttpClient _client;

    public GitHubTestHelper(IOptions<TestRepoOptions> options, IOptions<GitHubOptions> ghOptions)
    {
        _options = options.Value;
        var org = _options.Organization ?? ghOptions.Value.Organizations.Keys.First();
        var token = ghOptions.Value.Organizations[org].Token;

        _client = new HttpClient { BaseAddress = new Uri("https://api.github.com/") };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Revu", "1.0"));
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public ReviewRequest BuildRequest(int prId, string sourceBranch, string targetBranch = "refs/heads/main") =>
        new(_options.Provider, _options.Project, _options.RepositoryId, _options.RepositoryName,
            prId, sourceBranch, targetBranch, _options.Organization ?? "");

    public Task CleanComments(ReviewRequest req) => Task.CompletedTask;

    public async Task<int> GetRevuCommentCount(ReviewRequest req)
    {
        var (owner, repo) = ParseRepoId(req.RepositoryId);
        var comments = await _client.GetFromJsonAsync<List<ReviewComment>>(
            $"repos/{owner}/{repo}/pulls/{req.PullRequestId}/comments?per_page=100", JsonSerializerOptions.Web);

        return comments?.Count(c => c.Body?.Contains("<!-- revu:fp:") == true) ?? 0;
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

    private static (string Owner, string Repo) ParseRepoId(string repositoryId)
    {
        var parts = repositoryId.Split('/');
        return (parts[0], parts[1]);
    }

    public void Dispose() => _client.Dispose();

    private record Review(long Id, string? Body, string? State);
    private record ReviewComment(long Id, string? Body, string? Path, int? Line);
}
