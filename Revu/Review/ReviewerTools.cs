using System.ComponentModel;
using System.Text;

using Revu.CodeGraph;
using Revu.Git;

namespace Revu.Review;

public class ReviewerTools(IGitConnector git, ReviewRequest req, Diff diff, CodeGraphQuery? codeGraphQuery = null)
{
    [Description("Read full file contents. Pass multiple paths to fetch them all in one call.")]
    public Task<string> FetchFile(
        [Description("One or more file paths relative to repository root")]
        string[] paths)
    {
        paths = paths.DistinctBy(p => p.TrimStart('/'), StringComparer.OrdinalIgnoreCase).ToArray();
        return BatchAsync(paths, async path =>
        {
            var normalized = path.TrimStart('/');

            var cached = diff.Files.FirstOrDefault(f =>
                f.Path.TrimStart('/').Equals(normalized, StringComparison.OrdinalIgnoreCase));
            if (cached?.Content is not null)
                return (path, cached.Content);

            var content = await git.GetFile(req, path);
            return (path, content ?? $"File not found: {path}. Use ListDirectory to discover valid file paths.");
        });
    }

    [Description("List all files under a directory path.")]
    public async Task<string> ListDirectory([Description("Directory path relative to repository root, e.g. src/Services. Use \"src\" or \"/\" to list the root.")] string path)
    {
        if (path is "." or "./" or "")
            path = "/";
        var files = await git.ListFiles(req, path);
        return files.Count == 0 ? "No files found." : string.Join('\n', files);
    }

    [Description(
        "Search the repository for identifiers. Pass multiple queries to search them all in one call. " +
        "Each query must be a single identifier (class name, method name, variable name) — " +
        "never code snippets, keywords, or multi-word phrases. " +
        "ADO search tokenizes multi-word queries and matches each word separately, " +
        "so code snippets return noise, not useful results. " +
        "Searches the indexed default branch — code only in this PR won't appear.")]
    public Task<string> SearchCode(
        [Description("One or more identifiers to search for, e.g. [\"OrderService\", \"CalculateDiscount\"]")]
        string[] queries)
    {
        queries = queries.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return BatchAsync(queries, async query =>
        {
            var sanitized = query.Trim().Trim('"', '\'', '(', ')', '[', ']');
            if (string.IsNullOrWhiteSpace(sanitized))
                return (query, "Empty query.");

            if (sanitized.Any(c => ";{}=<>!&|,".Contains(c)) || sanitized.Split(' ').Length > 2)
                return (query,
                    "Bad query — pass a single identifier (e.g. \"OrderService\"), not a code snippet.");

            const int maxLocalResults = 5;
            var localMatches = diff.Files
                .Where(f => f.Content is not null)
                .SelectMany(f => f.Content!.Split('\n')
                    .Select((line, idx) => (f.Path, Line: idx + 1, Text: line))
                    .Where(x => x.Text.Contains(sanitized, StringComparison.OrdinalIgnoreCase)))
                .Take(maxLocalResults)
                .ToList();

            var results = await git.SearchCode(req, sanitized);

            // ADO treats PascalCase as atomic tokens — suffix wildcard needed for partial matches
            if (results.Count == 0 && !sanitized.Contains('*') && req.Provider == GitProvider.Ado)
                results = await git.SearchCode(req, sanitized + "*");

            if (localMatches.Count == 0 && results.Count == 0)
                return (query, "No results.");

            var sb = new StringBuilder();
            foreach (var m in localMatches)
                sb.AppendLine($"[PR] {m.Path}:{m.Line}: {m.Text.Trim()}");
            foreach (var r in results)
                sb.AppendLine(r.Line > 0 ? $"{r.Path}:{r.Line}: {r.Snippet}" : $"{r.Path} ({r.Snippet})");
            return (query, sb.ToString().TrimEnd());
        });
    }

    [Description(
        "Query the structural code graph. Use BEFORE fetching files to understand how code " +
        "connects. Supports: callers (who calls this?), implementations (who implements this " +
        "interface?), dependents (what files depend on this one?), outline (symbols in a file), " +
        "hierarchy (inheritance chain).")]
    public Task<string> QueryCodeGraph(
        [Description("callers | implementations | dependents | outline | hierarchy")]
        string queryKind,
        [Description("Symbol name (e.g. IGitConnector, Calculate) or file path (for outline/dependents)")]
        string target,
        [Description("File path to disambiguate when multiple symbols share a name")]
        string? filePath = null)
    {
        if (codeGraphQuery is null)
            return Task.FromResult("No code graph available for this repository. Use SearchCode and FetchFile instead.");

        return Task.FromResult(codeGraphQuery.Execute(queryKind, target, filePath));
    }

    private static async Task<string> BatchAsync<T>(
        T[] items, Func<T, Task<(string key, string value)>> process)
    {
        var results = await Task.WhenAll(items.Select(process));
        if (results.Length == 1)
            return results[0].value;

        var sb = new StringBuilder();
        foreach (var (key, value) in results)
            sb.AppendLine($"### {key}\n{value}\n");
        return sb.ToString().TrimEnd();
    }
}
