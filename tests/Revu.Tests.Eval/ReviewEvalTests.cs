using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

using Revu.Tests.Eval.Evaluators;
using Revu.Tests.Eval.TestHelpers;

using Xunit.Abstractions;

namespace Revu.Tests.Eval;

public class ReviewEvalTests(EvalFixture fixture, ITestOutputHelper output) : IClassFixture<EvalFixture>
{
    public static TheoryData<string> Fixtures
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var file in Directory.GetFiles(
                         Path.Combine(AppContext.BaseDirectory, "TestData"), "*.json"))
                data.Add(Path.GetFileName(file));
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Evaluate(string fixtureName)
    {
        // Arrange
        var data = EvalFixture.LoadFixture(fixtureName);
        var git = new FixtureGitConnector(data.Files);
        var (reviewer, capture) = fixture.CreateReviewer(git);

        // Act — run the actual review
        var result = await reviewer.Review(data.Request, data.Diff, data.Config, git);

        // Build evaluation inputs
        var capturedMessages = capture.Messages;
        var evalContext = new ReviewEvaluationContext(result, data.Diff, git, capturedMessages, data.Expectations);
        var messages = capturedMessages.Where(m => m.Role == ChatRole.User).ToList();
        Assert.NotNull(capture.LastResponse);

        // Evaluate — ScenarioRun handles evaluators, judge client, and response caching
        await using var run = await fixture.Reporting.CreateScenarioRunAsync(
            scenarioName: "ReviewEvalTests.Evaluate",
            iterationName: fixtureName);

        var evalResult = await run.EvaluateAsync(
            messages, capture.LastResponse, additionalContext: [evalContext]);

        // Output all metrics
        output.WriteLine($"=== {fixtureName} ===\n");

        foreach (var name in fixture.MetricNames)
        {
            if (!evalResult.TryGet<EvaluationMetric>(name, out var metric))
                continue;

            var value = metric switch
            {
                NumericMetric n => n.Value?.ToString() ?? "null",
                BooleanMetric b => b.Value?.ToString() ?? "null",
                StringMetric s => s.Value ?? "null",
                _ => "?"
            };

            var status = metric.Interpretation?.Failed == true ? "FAIL" : "OK";
            output.WriteLine($"  [{status}] {name} = {value}");
            if (metric.Reason is not null)
                output.WriteLine($"         {metric.Reason}");
        }

        // Output tool call details
        output.WriteLine($"\nTool calls via FixtureGitConnector:");
        output.WriteLine($"  GetFile: {git.GetFileCalls.Count}");
        foreach (var path in git.GetFileCalls)
            output.WriteLine($"    - {path}");
        output.WriteLine($"  SearchCode: {git.SearchCodeCalls.Count}");
        foreach (var (q, count) in git.SearchCodeCalls)
            output.WriteLine($"    - \"{q}\" → {count} result(s)");
        output.WriteLine($"  ListFiles: {git.ListFilesCalls.Count}");

        // Output findings
        output.WriteLine($"\nFindings ({result.Findings.Count}):");
        foreach (var f in result.Findings)
            output.WriteLine($"  [{f.Severity}] {f.FilePath}:{f.StartLine}-{f.EndLine} — {f.Message}");

        // Assert — any metric with failed=true should fail the test
        var failures = new List<EvaluationMetric>();
        foreach (var name in fixture.MetricNames)
        {
            if (evalResult.TryGet<EvaluationMetric>(name, out var m) && m?.Interpretation?.Failed == true)
                failures.Add(m);
        }

        if (failures.Count > 0)
        {
            var failureDetails = string.Join("\n",
                failures.Select(m => $"  {m.Name}: {m.Reason}"));
            Assert.Fail($"Evaluation failed for {fixtureName}:\n{failureDetails}");
        }
    }
}
