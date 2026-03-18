using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace Aporia.Tests.Eval.Evaluators;

/// <summary>
/// Observation-only evaluator that reports how the agent operated: mode (explorer vs
/// direct-only), orchestrator function calls, and tool usage via <see cref="TestHelpers.FixtureGitConnector"/>.
/// Never fails — agent behavior is telemetry, not a pass/fail criterion.
/// </summary>
public sealed class AgentBehaviorEvaluator : IEvaluator
{
    public const string AgentModeMetric = "AgentMode";
    public const string ExplorationCountMetric = "ExplorationCount";
    public const string DirectToolCountMetric = "DirectToolCount";
    public const string GetFileCallsMetric = "GetFileCalls";
    public const string SearchCodeCallsMetric = "SearchCodeCalls";
    public const string WastedSearchCodeCallsMetric = "WastedSearchCodeCalls";
    public const string SearchCodeEfficiencyMetric = "SearchCodeEfficiency";
    public const string ListFilesCallsMetric = "ListFilesCalls";
    public const string TotalToolCallsMetric = "TotalToolCalls";

    public IReadOnlyCollection<string> EvaluationMetricNames =>
        [AgentModeMetric, ExplorationCountMetric, DirectToolCountMetric,
         GetFileCallsMetric, SearchCodeCallsMetric, WastedSearchCodeCallsMetric,
         SearchCodeEfficiencyMetric, ListFilesCallsMetric, TotalToolCallsMetric];

    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        var ctx = additionalContext?.OfType<ReviewEvaluationContext>().FirstOrDefault()
                  ?? throw new InvalidOperationException($"{nameof(ReviewEvaluationContext)} is required.");

        // Orchestrator function calls (from captured chat messages)
        var toolCalls = ctx.CapturedMessages
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .ToList();

        // Match Explore calls to their results — rejected calls return the budget-exhausted message
        var toolResults = ctx.CapturedMessages
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .ToList();

        var rejectedCallIds = toolResults
            .Where(r => r.Result?.ToString()?.Contains("budget exhausted", StringComparison.OrdinalIgnoreCase) == true)
            .Select(r => r.CallId)
            .ToHashSet();

        var explorations = toolCalls.Count(tc =>
            string.Equals(tc.Name, "Explore", StringComparison.OrdinalIgnoreCase)
            && !rejectedCallIds.Contains(tc.CallId));

        var rejectedExplorations = toolCalls.Count(tc =>
            string.Equals(tc.Name, "Explore", StringComparison.OrdinalIgnoreCase)
            && rejectedCallIds.Contains(tc.CallId));

        var directTools = toolCalls.Count - explorations - rejectedExplorations;
        var isMultiAgent = explorations > 0;

        // Git connector tool calls (from fixture)
        var git = ctx.Git;
        var searchCalls = git.SearchCodeCalls.Count;
        var wastedSearchCalls = git.SearchCodeCalls.Count(c => c.ResultCount == 0);
        var searchEfficiency = searchCalls > 0 ? (double)(searchCalls - wastedSearchCalls) / searchCalls : 1.0;
        var totalToolCalls = git.GetFileCalls.Count + searchCalls + git.ListFilesCalls.Count;

        // Check expectedMode from fixture expectations
        var expectedMode = ctx.Expectations?.ExpectedMode;
        var actualMode = isMultiAgent ? "multi-agent" : "single-agent";
        var modeMatches = expectedMode is null
            || string.Equals(expectedMode, actualMode, StringComparison.OrdinalIgnoreCase);

        return new(new EvaluationResult(
            new StringMetric(AgentModeMetric, actualMode,
                reason: modeMatches
                    ? (isMultiAgent
                        ? $"Reviewer spawned {explorations} exploration(s)"
                          + (rejectedExplorations > 0 ? $" ({rejectedExplorations} rejected — budget exhausted)" : "")
                        : "Reviewer handled review with direct tool calls only")
                    : $"Expected {expectedMode} but got {actualMode}")
                .Rated(modeMatches ? EvaluationRating.Inconclusive : EvaluationRating.Poor,
                    failed: !modeMatches),
            new NumericMetric(ExplorationCountMetric, explorations)
                .Rated(EvaluationRating.Inconclusive),
            new NumericMetric(DirectToolCountMetric, directTools)
                .Rated(EvaluationRating.Inconclusive),
            new NumericMetric(GetFileCallsMetric, git.GetFileCalls.Count)
                .Rated(EvaluationRating.Inconclusive),
            new NumericMetric(SearchCodeCallsMetric, searchCalls)
                .Rated(EvaluationRating.Inconclusive),
            new NumericMetric(WastedSearchCodeCallsMetric, wastedSearchCalls,
                reason: wastedSearchCalls > 0
                    ? $"{wastedSearchCalls} search(es) returned zero results: {string.Join(", ", git.SearchCodeCalls.Where(c => c.ResultCount == 0).Select(c => $"\"{c.Query}\""))}"
                    : "All searches returned results")
                .Rated(EvaluationRating.Inconclusive),
            new NumericMetric(SearchCodeEfficiencyMetric, searchEfficiency,
                reason: $"{searchCalls - wastedSearchCalls}/{searchCalls} searches productive")
                .Rated(EvaluationRating.Inconclusive),
            new NumericMetric(ListFilesCallsMetric, git.ListFilesCalls.Count)
                .Rated(EvaluationRating.Inconclusive),
            new NumericMetric(TotalToolCallsMetric, totalToolCalls,
                reason: $"{totalToolCalls} total tool calls ({git.GetFileCalls.Count} GetFile, {searchCalls} SearchCode, {git.ListFilesCalls.Count} ListFiles)")
                .Rated(EvaluationRating.Inconclusive)));
    }
}
