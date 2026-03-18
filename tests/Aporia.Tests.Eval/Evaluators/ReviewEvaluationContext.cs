using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

using Aporia.Tests.Eval.TestHelpers;

namespace Aporia.Tests.Eval.Evaluators;

/// <summary>
/// Carries Aporia domain data into evaluators. Each evaluator pulls what it needs via
/// <c>additionalContext?.OfType&lt;ReviewEvaluationContext&gt;().FirstOrDefault()</c>.
/// </summary>
public sealed class ReviewEvaluationContext(
    ReviewResult result,
    Diff diff,
    FixtureGitConnector git,
    IReadOnlyList<ChatMessage> capturedMessages,
    FixtureExpectations? expectations = null)
    : EvaluationContext("ReviewEvaluation", "Aporia review evaluation context")
{
    public ReviewResult Result => result;
    public Diff Diff => diff;
    public FixtureGitConnector Git => git;
    public IReadOnlyList<ChatMessage> CapturedMessages => capturedMessages;
    public FixtureExpectations? Expectations => expectations;
}

internal static class EvaluationMetricExtensions
{
    public static T Rated<T>(this T metric, EvaluationRating rating, bool failed = false)
        where T : EvaluationMetric
    {
        metric.Interpretation = new EvaluationMetricInterpretation(rating, failed);
        return metric;
    }
}
