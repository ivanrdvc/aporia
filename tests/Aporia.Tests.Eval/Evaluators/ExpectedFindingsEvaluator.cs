using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

using Aporia.Tests.Eval.TestHelpers;

namespace Aporia.Tests.Eval.Evaluators;

/// <summary>
/// Deterministic evaluator that checks whether the agent found the planted bugs
/// defined in the fixture's <see cref="FixtureExpectations.ExpectedFindings"/>.
/// Matches by file path (normalized) + keyword presence in the finding message.
/// </summary>
public sealed class ExpectedFindingsEvaluator : IEvaluator
{
    public const string RecallMetric = "FindingRecall";
    public const string MissedCountMetric = "ExpectedFindingsMissed";
    public const string ExtraCountMetric = "ExtraFindings";

    public IReadOnlyCollection<string> EvaluationMetricNames =>
        [RecallMetric, MissedCountMetric, ExtraCountMetric];

    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        var ctx = additionalContext?.OfType<ReviewEvaluationContext>().FirstOrDefault()
                  ?? throw new InvalidOperationException($"{nameof(ReviewEvaluationContext)} is required.");

        var expected = ctx.Expectations?.ExpectedFindings;
        var expectNone = ctx.Expectations?.ExpectNoFindings == true;

        // Expect zero findings — fail if any false positives appear
        if (expectNone)
            return new(NoFindingsExpected(ctx.Result.Findings.Count));

        // No expectations defined — skip, all metrics inconclusive
        if (expected is null || expected.Count == 0)
            return new(NoExpectations());

        var actual = ctx.Result.Findings;
        var matchedExpected = new HashSet<int>();
        var matchedActual = new HashSet<int>();

        for (var i = 0; i < expected.Count; i++)
        {
            var exp = expected[i];
            var normalizedExpPath = exp.File.TrimStart('/');

            for (var j = 0; j < actual.Count; j++)
            {
                var finding = actual[j];
                var normalizedActualPath = finding.FilePath.TrimStart('/');
                if (!normalizedActualPath.Equals(normalizedExpPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (finding.Message.Contains(exp.Keyword, StringComparison.OrdinalIgnoreCase))
                {
                    matchedExpected.Add(i);
                    matchedActual.Add(j);
                    break;
                }
            }
        }

        var found = matchedExpected.Count;
        var missed = expected.Count - found;
        var recall = (double)found / expected.Count;

        // Extra findings: actual findings not matched to any expected finding
        var extra = actual.Count - matchedActual.Count;

        // Split misses into required vs optional
        var missedRequired = expected
            .Where((e, i) => e.Required && !matchedExpected.Contains(i))
            .Select(e => $"[required] {e.File}: {e.Description}")
            .ToList();

        var missedOptional = expected
            .Where((e, i) => !e.Required && !matchedExpected.Contains(i))
            .Select(e => $"[optional] {e.File}: {e.Description}")
            .ToList();

        var missedDescriptions = missedRequired.Concat(missedOptional).ToList();

        // Only fail on required misses — optional findings beyond the comment cap are bonus
        var hasRequiredMisses = missedRequired.Count > 0;

        return new(new EvaluationResult(
            new NumericMetric(RecallMetric, recall,
                    reason: $"{found}/{expected.Count} expected bugs found ({recall:P0})")
                .Rated(recall switch
                {
                    >= 1.0 => EvaluationRating.Exceptional,
                    >= 0.75 => EvaluationRating.Good,
                    >= 0.5 => EvaluationRating.Average,
                    _ => EvaluationRating.Poor
                }, failed: hasRequiredMisses),
            new NumericMetric(MissedCountMetric, missed,
                    reason: missed > 0
                        ? $"Missed: {string.Join("; ", missedDescriptions)}"
                        : "All expected bugs found")
                .Rated(hasRequiredMisses ? EvaluationRating.Poor : EvaluationRating.Good,
                    failed: hasRequiredMisses),
            new NumericMetric(ExtraCountMetric, extra)
                .Rated(EvaluationRating.Inconclusive)));
    }

    private static EvaluationResult NoFindingsExpected(int actualCount) => new(
        new NumericMetric(RecallMetric, 1.0, reason: "No findings expected — recall is perfect by definition")
            .Rated(EvaluationRating.Good),
        new NumericMetric(MissedCountMetric, 0, reason: "No findings expected")
            .Rated(EvaluationRating.Good),
        new NumericMetric(ExtraCountMetric, actualCount,
                reason: actualCount > 0
                    ? $"{actualCount} false positive(s) — expected zero findings"
                    : "No false positives")
            .Rated(actualCount == 0 ? EvaluationRating.Good : EvaluationRating.Poor,
                failed: actualCount > 0));

    private static EvaluationResult NoExpectations() => new(
        new NumericMetric(RecallMetric, reason: "No expected findings defined").Rated(EvaluationRating.Inconclusive),
        new NumericMetric(MissedCountMetric).Rated(EvaluationRating.Inconclusive),
        new NumericMetric(ExtraCountMetric).Rated(EvaluationRating.Inconclusive));
}
