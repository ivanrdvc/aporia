using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Aporia.Git;

namespace Aporia.DocWatch;

/// <summary>
/// GitHub implementation of <see cref="IDocPublisher"/>. Uses the GitHub REST API
/// (contents, refs, pulls) to publish documentation changes.
/// </summary>
public class GitHubDocPublisher(HttpClient http) : IDocPublisher
{
    public async Task<string> CreateBranch(string repoId, string baseBranch, string newBranch, long? installationId = null)
    {
        var (owner, repo) = ParseRepoId(repoId);

        var refResponse = await GetAsync<GitRef>(
            $"repos/{owner}/{repo}/git/ref/heads/{baseBranch}", installationId);
        var sha = refResponse!.Object.Sha;

        using var response = await PostAsync(
            $"repos/{owner}/{repo}/git/refs",
            new { @ref = $"refs/heads/{newBranch}", sha }, installationId);
        response.EnsureSuccessStatusCode();

        return sha;
    }

    // TODO: implement via Git Trees API (create blobs → create tree → create commit → update ref)
    public Task PushFiles(string repoId, string branch, IReadOnlyList<DocFile> files, string commitMessage, long? installationId = null)
        => throw new NotImplementedException("GitHub PushFiles not yet implemented — use Git Trees API");

    public async Task<int> CreatePullRequest(string repoId, string sourceBranch, string targetBranch, string title, string body, long? installationId = null)
    {
        var (owner, repo) = ParseRepoId(repoId);

        using var response = await PostAsync(
            $"repos/{owner}/{repo}/pulls",
            new { title, body, head = sourceBranch, @base = targetBranch }, installationId);
        response.EnsureSuccessStatusCode();

        var pr = await response.Content.ReadFromJsonAsync<PrResponse>(JsonSerializerOptions.Web);
        var prNumber = pr!.Number;

        using var _ = await PostAsync(
            $"repos/{owner}/{repo}/issues/{prNumber}/labels",
            new { labels = new[] { DocWatchConstants.Label } }, installationId);

        return prNumber;
    }

    public async Task UpdatePullRequest(string repoId, int prNumber, string? title = null, string? body = null, long? installationId = null)
    {
        var (owner, repo) = ParseRepoId(repoId);

        var payload = new Dictionary<string, string>();
        if (title is not null) payload["title"] = title;
        if (body is not null) payload["body"] = body;

        using var response = await SendAsync(HttpMethod.Patch,
            $"repos/{owner}/{repo}/pulls/{prNumber}", payload, installationId);
        response.EnsureSuccessStatusCode();
    }

    public async Task<(int Number, string Branch, string Body)?> FindOpenPullRequest(string repoId, string label, long? installationId = null)
    {
        var (owner, repo) = ParseRepoId(repoId);

        using var response = await SendAsync(HttpMethod.Get,
            $"repos/{owner}/{repo}/pulls?state=open&head={owner}:{DocWatchConstants.BranchName}", null, installationId);

        if (!response.IsSuccessStatusCode)
            return null;

        var prs = await response.Content.ReadFromJsonAsync<List<PrResponse>>(JsonSerializerOptions.Web);
        var match = prs?.FirstOrDefault();

        if (match is null) return null;

        return (match.Number, match.Head.Ref, match.Body ?? "");
    }

    public async Task AddComment(string repoId, int prNumber, string body, long? installationId = null)
    {
        var (owner, repo) = ParseRepoId(repoId);

        using var response = await PostAsync(
            $"repos/{owner}/{repo}/issues/{prNumber}/comments",
            new { body }, installationId);
        response.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body, long? installationId)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        if (installationId is { } id)
            request.Options.Set(GitHubAuthHandler.InstallationIdKey, id);
        return await http.SendAsync(request);
    }

    private Task<HttpResponseMessage> PostAsync(string url, object body, long? installationId) =>
        SendAsync(HttpMethod.Post, url, body, installationId);

    private async Task<T?> GetAsync<T>(string url, long? installationId)
    {
        using var response = await SendAsync(HttpMethod.Get, url, null, installationId);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonSerializerOptions.Web);
    }

    private static (string Owner, string Repo) ParseRepoId(string repoId)
    {
        var parts = repoId.Split("__");
        return (parts[0], parts[1]);
    }

    private record GitRef(GitObject Object);
    private record GitObject(string Sha);
    private record ContentResponse(string? Sha);
    private record PrResponse(int Number, string? Body, RefInfo Head, RefInfo Base, List<LabelInfo>? Labels = null);
    private record RefInfo(string Ref, string Sha);
    private record LabelInfo(string Name);
}
