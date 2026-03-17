using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Revu.Infra.Telemetry;

public static class Telemetry
{
    public const string ServiceName = "Revu";

    public static readonly Meter Meter = new(ServiceName);

    // Review-level (recorded in ReviewFunction)
    public static readonly Counter<int> ReviewsProcessed = Meter.CreateCounter<int>(
        "revu.reviews.processed", description: "Total reviews processed");

    public static readonly Histogram<int> DiffFiles = Meter.CreateHistogram<int>(
        "revu.diff.files", description: "Number of files in the diff");

    public static readonly Histogram<int> DiffSize = Meter.CreateHistogram<int>(
        "revu.diff.size", "chars", "Total diff size in characters");

    public static void RecordReview(ReviewRequest req, Diff diff)
    {
        var tags = new TagList { { "project", req.Project }, { "repository", req.RepositoryId } };
        ReviewsProcessed.Add(1, tags);
        DiffFiles.Record(diff.Files.Count, tags);
        DiffSize.Record(diff.Files.Sum(f => f.Patch?.Length ?? 0), tags);
    }

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

    public static readonly Counter<int> AgentMaxTurnsHit = Meter.CreateCounter<int>(
        "revu.agent.max_turns_hit", description: "Agent runs that ended with a non-completed finish reason");
}
