using System.Text;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace Revu.Tests.Eval.Evaluators;

/// <summary>
/// LLM-as-judge evaluator that rates each finding on actionability and specificity.
/// Requires a <see cref="ChatConfiguration"/> with a judge model.
/// Batches all findings into a single prompt to minimize cost.
/// </summary>
public sealed class FindingQualityEvaluator : IEvaluator
{
    public const string ActionabilityMetric = "Actionability";
    public const string SpecificityMetric = "Specificity";
    public const string CodeFormattingMetric = "CodeFormatting";

    public IReadOnlyCollection<string> EvaluationMetricNames =>
        [ActionabilityMetric, SpecificityMetric, CodeFormattingMetric];

    public async ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        var ctx = additionalContext?.OfType<ReviewEvaluationContext>().FirstOrDefault()
                  ?? throw new InvalidOperationException($"{nameof(ReviewEvaluationContext)} is required.");

        if (chatConfiguration is null)
            return NoJudge("No ChatConfiguration provided — skipping LLM quality evaluation");

        var findings = ctx.Result.Findings;

        if (findings.Count == 0)
            return NoFindings();

        // Build diff lookup: file path → patch
        var patches = ctx.Diff.Files.ToDictionary(
            f => f.Path.TrimStart('/'),
            f => f.Patch ?? "(no diff available)",
            StringComparer.OrdinalIgnoreCase);

        var prompt = BuildPrompt(findings, patches);

        var response = await chatConfiguration.ChatClient.GetResponseAsync<JudgeResponse>(
            prompt,
            options: new() { Temperature = 0f },
            cancellationToken: cancellationToken);

        return BuildResult(response.Result);
    }

    private static string BuildPrompt(List<Finding> findings, Dictionary<string, string> patches)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
                      You are evaluating code review comments. For each comment below, rate it on two dimensions (1-5):

                      **Actionability** — Does the comment explain what to fix, not just what is wrong?
                      1 = States the problem with no guidance ("This is bad")
                      3 = Identifies the issue and hints at direction ("Consider using parameterized queries")
                      5 = Explains the problem and gives a concrete fix or pattern to follow

                      **Specificity** — Does the comment reference specific code elements (function names, variables, patterns)?
                      1 = Completely generic ("This function has an issue")
                      3 = References the general area ("The SQL query in this method concatenates user input")
                      5 = Pinpoints exact code elements ("The `userId` parameter is concatenated into the SQL string on line 72")

                      **CodeFormatting** — When the comment mentions code identifiers, function names, types, or literals, are they wrapped in backticks?
                      1 = References code elements as plain text throughout ("the userId parameter in GetOrderHistory")
                      3 = Mix of formatted and unformatted code references
                      5 = All code references use backtick formatting ("the `userId` parameter in `GetOrderHistory`")
                      Note: if a comment has no code references (pure prose), score 3 (neutral).

                      ---
                      """);

        for (var i = 0; i < findings.Count; i++)
        {
            var f = findings[i];
            var normalizedPath = f.FilePath.TrimStart('/');

            sb.AppendLine($"### Comment {i + 1}");
            sb.AppendLine($"File: {f.FilePath}:{f.StartLine}{(f.EndLine.HasValue ? $"-{f.EndLine}" : "")}");
            sb.AppendLine($"Severity: {f.Severity}");
            sb.AppendLine($"Message: {f.Message}");

            if (patches.TryGetValue(normalizedPath, out var patch))
            {
                sb.AppendLine("Relevant diff:");
                sb.AppendLine($"```\n{Truncate(patch, 2000)}\n```");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static EvaluationResult BuildResult(JudgeResponse? envelope)
    {
        if (envelope?.Scores is not { Count: > 0 })
            return NoJudge("Judge returned empty or unparseable response");

        var scores = envelope.Scores;

        var avgActionability = scores.Average(s => s.Actionability);
        var avgSpecificity = scores.Average(s => s.Specificity);
        var avgFormatting = scores.Average(s => s.CodeFormatting);

        return new(
            new NumericMetric(ActionabilityMetric, avgActionability,
                    reason: $"Average {avgActionability:F1}/5 across {scores.Count} finding(s)")
                .Rated(avgActionability switch
                {
                    >= 4.0 => EvaluationRating.Good,
                    >= 3.0 => EvaluationRating.Average,
                    _ => EvaluationRating.Poor
                }),
            new NumericMetric(SpecificityMetric, avgSpecificity,
                    reason: $"Average {avgSpecificity:F1}/5 across {scores.Count} finding(s)")
                .Rated(avgSpecificity switch
                {
                    >= 4.0 => EvaluationRating.Good,
                    >= 3.0 => EvaluationRating.Average,
                    _ => EvaluationRating.Poor
                }),
            new NumericMetric(CodeFormattingMetric, avgFormatting,
                    reason: $"Average {avgFormatting:F1}/5 across {scores.Count} finding(s)")
                .Rated(avgFormatting switch
                {
                    >= 4.0 => EvaluationRating.Good,
                    >= 3.0 => EvaluationRating.Average,
                    _ => EvaluationRating.Poor
                }));
    }

    private static EvaluationResult NoFindings() => new(
        new NumericMetric(ActionabilityMetric, reason: "No findings to evaluate").Rated(EvaluationRating.Inconclusive),
        new NumericMetric(SpecificityMetric, reason: "No findings to evaluate").Rated(EvaluationRating.Inconclusive),
        new NumericMetric(CodeFormattingMetric, reason: "No findings to evaluate").Rated(EvaluationRating.Inconclusive));

    private static EvaluationResult NoJudge(string reason) => new(
        new NumericMetric(ActionabilityMetric, reason: reason).Rated(EvaluationRating.Inconclusive),
        new NumericMetric(SpecificityMetric, reason: reason).Rated(EvaluationRating.Inconclusive),
        new NumericMetric(CodeFormattingMetric, reason: reason).Rated(EvaluationRating.Inconclusive));

    private static string Truncate(string s, int maxLength) =>
        s.Length <= maxLength ? s : s[..maxLength] + "...";

    private record JudgeResponse(List<FindingScore> Scores);
    private record FindingScore(int Actionability, int Specificity, int CodeFormatting);
}
