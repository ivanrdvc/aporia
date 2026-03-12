using System.Diagnostics.Metrics;

namespace Revu.Infra.Telemetry;

public static class Telemetry
{
    public const string ServiceName = "Revu";

    public static readonly Meter Meter = new(ServiceName);

    // Review-level (recorded in middleware)
    public static readonly Counter<int> ReviewsProcessed = Meter.CreateCounter<int>(
        "revu.reviews.processed", description: "Total reviews processed");

    public static readonly Histogram<double> ReviewDuration = Meter.CreateHistogram<double>(
        "revu.review.duration", "s", "End-to-end review duration");

    // Diff stats (recorded in Reviewer)
    public static readonly Histogram<int> DiffFiles = Meter.CreateHistogram<int>(
        "revu.diff.files", description: "Number of files in the diff");

    public static readonly Histogram<int> DiffSize = Meter.CreateHistogram<int>(
        "revu.diff.size", "chars", "Total diff size in characters");

    // Findings (recorded in Reviewer)
    public static readonly Counter<int> FindingsGenerated = Meter.CreateCounter<int>(
        "revu.findings.generated", description: "Findings produced by LLM before filtering");

    public static readonly Counter<int> FindingsPosted = Meter.CreateCounter<int>(
        "revu.findings.posted", description: "Findings posted after filtering and capping");

    // Agent-level (recorded in CoreStrategy)
    public static readonly Counter<int> Explorations = Meter.CreateCounter<int>(
        "revu.agent.explorations", description: "Explorer agent dispatches");

    // Failure tracking (recorded in CoreStrategy)
    public static readonly Counter<int> ParseFailures = Meter.CreateCounter<int>(
        "revu.review.parse_failures", description: "Reviews where structured output failed to parse");

    public static readonly Counter<int> ExplorationFailures = Meter.CreateCounter<int>(
        "revu.agent.exploration_failures", description: "Explorer invocations that failed or returned invalid output");
}
