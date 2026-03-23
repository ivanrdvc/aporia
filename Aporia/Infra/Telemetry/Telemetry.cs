using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Aporia.Infra.Telemetry;

public static class Telemetry
{
    public const string ServiceName = "Aporia";

    public static readonly Meter Meter = new(ServiceName);

    // Review-level (recorded in ReviewFunction)
    public static readonly Counter<int> ReviewsProcessed = Meter.CreateCounter<int>(
        "aporia.reviews.processed", description: "Total reviews processed");

    public static readonly Histogram<int> DiffFiles = Meter.CreateHistogram<int>(
        "aporia.diff.files", description: "Number of files in the diff");

    public static readonly Histogram<int> DiffSize = Meter.CreateHistogram<int>(
        "aporia.diff.size", "chars", "Total diff size in characters");

    public static void RecordReview(ReviewRequest req, Diff diff, string strategy)
    {
        var tags = new TagList { { "project", req.Project }, { "repository", req.RepositoryId }, { "strategy", strategy } };
        ReviewsProcessed.Add(1, tags);
        DiffFiles.Record(diff.Files.Count, tags);
        DiffSize.Record(diff.Files.Sum(f => f.Patch?.Length ?? 0), tags);
    }

    // Findings (recorded in Reviewer)
    public static readonly Counter<int> FindingsGenerated = Meter.CreateCounter<int>(
        "aporia.findings.generated", description: "Findings produced by LLM before filtering");

    public static readonly Counter<int> FindingsPosted = Meter.CreateCounter<int>(
        "aporia.findings.posted", description: "Findings posted after filtering and capping");

    // Agent-level (recorded in CoreStrategy)
    public static readonly Counter<int> Explorations = Meter.CreateCounter<int>(
        "aporia.agent.explorations", description: "Explorer agent dispatches");

    // Failure tracking (recorded in CoreStrategy)
    public static readonly Counter<int> ExplorationFailures = Meter.CreateCounter<int>(
        "aporia.agent.exploration_failures", description: "Explorer invocations that failed or returned invalid output");

    // Copilot strategy
    public static readonly Counter<int> CopilotExtractionFailures = Meter.CreateCounter<int>(
        "aporia.copilot.extraction_failures", description: "Copilot reviews where structured extraction failed");
}
