using System.Collections.Concurrent;

using Aporia.Git;

namespace Aporia.Tests.Eval.TestHelpers;

/// <summary>
/// Stub <see cref="IGitConnector"/> that serves pre-captured fixture data
/// and records all tool calls for test assertions.
/// </summary>
public sealed class FixtureGitConnector(Dictionary<string, string> files) : IGitConnector
{
    public ConcurrentBag<string> GetFileCalls { get; } = [];
    public ConcurrentBag<(string Query, int ResultCount)> SearchCodeCalls { get; } = [];
    public ConcurrentBag<string> ListFilesCalls { get; } = [];

    public Task<ProjectConfig> GetConfig(ReviewRequest req) =>
        throw new NotSupportedException("Config is passed directly to Review().");

    public Task<Diff> GetDiff(ReviewRequest req, ProjectConfig config) =>
        throw new NotSupportedException("Diff is passed directly to Review().");

    public Task PostReview(ReviewRequest req, Diff diff, ReviewResult result) =>
        throw new NotSupportedException("Eval inspects ReviewResult directly.");

    public Task<string?> GetFile(ReviewRequest req, string path)
    {
        GetFileCalls.Add(path);
        var normalized = "/" + path.TrimStart('/');
        files.TryGetValue(normalized, out var content);
        return Task.FromResult(content);
    }

    public Task<IReadOnlyList<string>> ListFiles(ReviewRequest req, string path, bool recursive = false)
    {
        ListFilesCalls.Add(path);
        var prefix = "/" + path.TrimStart('/').TrimEnd('/') + "/";
        var result = files.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(result);
    }

    public Task<PrContext> GetPrContext(ReviewRequest req) =>
        Task.FromResult(new PrContext("Test PR", null, []));

    public Task<ChatThreadContext?> GetChatThreadContext(ChatRequest req) =>
        Task.FromResult<ChatThreadContext?>(null);

    public Task PostChatReply(ChatRequest req, string body) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<SearchResult>> SearchCode(ReviewRequest req, string query)
    {
        var results = files
            .SelectMany(kv => kv.Value.Split('\n')
                .Select((line, idx) => new { kv.Key, Line = idx + 1, Text = line })
                .Where(x => x.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(x => new SearchResult(x.Key, x.Line, x.Text.Trim())))
            .ToList();
        SearchCodeCalls.Add((query, results.Count));
        return Task.FromResult<IReadOnlyList<SearchResult>>(results);
    }

    public Task<CloneCredentials> GetCloneCredentials(ReviewRequest req) =>
        throw new NotSupportedException("Eval uses fixture data, not local clones.");
}
