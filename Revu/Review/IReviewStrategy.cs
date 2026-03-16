using Revu.CodeGraph;
using Revu.Git;

namespace Revu.Review;

public interface IReviewStrategy
{
    Task<ReviewResult> Review(ReviewRequest req, Diff diff, ProjectConfig config, PrContext prContext, CodeGraphQuery? codeGraph = null, CancellationToken ct = default);
}

public static class ReviewStrategy
{
    public const string Core = "core";
    public const string Copilot = "copilot";
    public const string ClaudeCode = "claude-code";
}
