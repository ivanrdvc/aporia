using System.Diagnostics;
using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Revu.CodeGraph;
using Revu.Infra;
using Revu.Infra.Cosmos;
using Revu.Infra.Telemetry;

namespace Revu.Review;

public class Reviewer(Func<string, IReviewStrategy> strategyFactory, ICodeGraphStore codeGraphStore, IOptions<RevuOptions> options, ILogger<Reviewer> logger)
{
    private const int DefaultMaxComments = 5;
    private const int MaxInlineLineSpan = 20;
    private const int MaxCodeFixLineSpan = 15;

    public async Task<ReviewResult> Review(ReviewRequest req, Diff diff, ProjectConfig config, CancellationToken ct = default)
    {
        var tags = new TagList
        {
            { "project", req.Project },
            { "repository", req.RepositoryId }
        };

        var graphDocs = options.Value.EnableCodeGraph ? await codeGraphStore.GetAllAsync(req.RepositoryId) : null;
        var codeGraph = graphDocs is { Count: > 0 } ? new CodeGraphQuery(graphDocs) : null;

        var strategy = strategyFactory(config.Review.Strategy ?? ReviewStrategy.Core);
        var result = await strategy.Review(req, diff, config, codeGraph, ct);

        // Only allow findings on files actually changed in this PR — context files fetched by
        // investigators are read-only reference material and can't be anchored in ADO.
        var diffPaths = new HashSet<string>(
            diff.Files.Select(f => NormalizePath(f.Path)),
            StringComparer.OrdinalIgnoreCase);

        var inDiff = result.Findings
            .Where(f => diffPaths.Contains(NormalizePath(f.FilePath)))
            .ToList();

        var maxComments = config.Review.MaxComments ?? DefaultMaxComments;

        var picked = inDiff
            .OrderBy(f => f.Severity)
            .Take(maxComments)
            .ToHashSet();

        var inline = picked
            .Select(f => f with { FilePath = "/" + NormalizePath(f.FilePath) })
            .Select(f => f.CodeFix is not null && (f.EndLine ?? f.StartLine) - f.StartLine > MaxCodeFixLineSpan
                ? f with { CodeFix = null }
                : f)
            .Select(f => f.EndLine - f.StartLine > MaxInlineLineSpan ? f with { EndLine = f.StartLine } : f)
            .ToList();

        var summaryFindings = inDiff.Where(f => !picked.Contains(f)).ToList();

        var summary = BuildSummary(result.Summary, summaryFindings);

        if (inDiff.Count == 0)
            summary += "\n\nRevu reviewed the changes and found no issues.";

        logger.LogInformation("Findings: {Total} total, {Inline} inline, {Summary} summary, max {Max}",
            inDiff.Count, inline.Count, summaryFindings.Count, maxComments);

        Telemetry.FindingsGenerated.Add(inDiff.Count, tags);
        Telemetry.FindingsPosted.Add(inline.Count, tags);

        return new ReviewResult(inline, summary);
    }

    /// <summary>
    /// Strip leading slashes, whitespace, and stray characters that models sometimes
    /// inject into file paths in structured output (e.g. "/ src /Catalog .API/..." or "U/src/...").
    /// </summary>
    internal static string NormalizePath(string path)
    {
        path = path.Replace(" ", "");
        // Strip git diff prefix (a/, b/) or hallucinated single-char prefix (U/)
        if (path.Length > 2 && char.IsLetter(path[0]) && path[1] == '/')
            path = path[2..];
        return path.TrimStart('/');
    }

    private static string BuildSummary(string modelSummary, List<Finding> summaryFindings)
    {
        if (summaryFindings.Count == 0)
            return modelSummary;

        var sb = new StringBuilder(modelSummary);
        sb.Append($"\n\n<details>\n<summary>Additional findings ({summaryFindings.Count})</summary>\n\n");
        foreach (var f in summaryFindings)
            sb.AppendLine($"- `{f.FilePath}:{f.StartLine}` — {f.Message}");
        sb.Append("</details>");
        return sb.ToString();
    }
}
