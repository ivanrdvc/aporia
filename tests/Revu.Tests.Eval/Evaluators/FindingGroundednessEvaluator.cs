using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace Revu.Tests.Eval.Evaluators;

/// <summary>
/// Deterministic evaluator that checks whether findings are sane: target files in the changeset,
/// at least one finding produced, and no duplicate comments on the same location.
/// </summary>
public sealed class FindingGroundednessEvaluator : IEvaluator
{
    public const string FindingCountMetric = "FindingCount";
    public const string HallucinatedPathsMetric = "HallucinatedPaths";
    public const string DuplicateFindingsMetric = "DuplicateFindings";

    public IReadOnlyCollection<string> EvaluationMetricNames =>
        [FindingCountMetric, HallucinatedPathsMetric, DuplicateFindingsMetric];

    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        var ctx = additionalContext?.OfType<ReviewEvaluationContext>().FirstOrDefault()
                  ?? throw new InvalidOperationException($"{nameof(ReviewEvaluationContext)} is required.");

        var changedPaths = ctx.Diff.Files
            .Select(f => f.Path.TrimStart('/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var findings = ctx.Result.Findings;
        var expectNone = ctx.Expectations?.ExpectNoFindings == true;
        var hallucinated = findings.Count(f => !changedPaths.Contains(f.FilePath.TrimStart('/')));

        // Duplicate findings — same file + start line commented more than once
        var duplicates = findings
            .GroupBy(f => (f.FilePath.TrimStart('/').ToLowerInvariant(), f.StartLine))
            .Count(g => g.Count() > 1);

        // When the fixture expects zero findings, flip the logic: 0 is good, >0 is a fail (false positives).
        var countRating = expectNone
            ? (findings.Count == 0 ? EvaluationRating.Good : EvaluationRating.Poor)
            : (findings.Count > 0 ? EvaluationRating.Good : EvaluationRating.Poor);
        var countFailed = expectNone ? findings.Count > 0 : findings.Count == 0;

        return new(new EvaluationResult(
            new NumericMetric(FindingCountMetric, findings.Count,
                    reason: expectNone && findings.Count > 0
                        ? $"{findings.Count} false positive(s) — expected zero findings"
                        : null)
                .Rated(countRating, failed: countFailed),
            new NumericMetric(HallucinatedPathsMetric, hallucinated,
                reason: hallucinated > 0
                    ? $"{hallucinated} finding(s) target files not in the changeset"
                    : $"All {findings.Count} finding(s) target files in the changeset")
                .Rated(hallucinated == 0 ? EvaluationRating.Good : EvaluationRating.Unacceptable,
                    failed: hallucinated > 0),
            new NumericMetric(DuplicateFindingsMetric, duplicates,
                reason: duplicates > 0
                    ? $"{duplicates} file+line location(s) commented more than once"
                    : "No duplicate comment locations")
                .Rated(duplicates == 0 ? EvaluationRating.Good : EvaluationRating.Inconclusive)));
    }
}
