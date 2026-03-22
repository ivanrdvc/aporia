using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Aporia.Infra;
using Aporia.Infra.Cosmos;
using Aporia.Tests.Integration.Fixtures;

using Xunit.Abstractions;

namespace Aporia.Tests.Integration;

public class ReviewTests(
    AppFixture fixture,
    ITestOutputHelper output) : IntegrationTestBase(fixture, output)
{
    private ReviewRequest TestEvent => GetTestEvent();

    private ReviewRequest GetTestEvent()
    {
        var config = Services.GetRequiredService<IConfiguration>();
        var prId = config.GetValue<int>("TestTarget:PrId");
        var branch = config.GetValue<string>("TestTarget:Branch")!;
        return TestHelper.BuildRequest(prId, branch);
    }

    [Fact]
    public async Task Review_FullPipeline_PostsFindings()
    {
        await ResetReviewState(TestEvent);

        var config = ApplyStrategyOverride(await Git.GetConfig(TestEvent));
        var diff = await Git.GetDiff(TestEvent, config);
        var prContext = await Git.GetPrContext(TestEvent);
        var result = await Reviewer.Review(TestEvent, diff, config, prContext);

        var aporiaOptions = Services.GetRequiredService<IOptions<AporiaOptions>>();
        if (aporiaOptions.Value.EnablePostComments)
            await Git.PostReview(TestEvent, diff, result);

        await ReviewStore.SaveAsync(TestEvent, diff, ReviewStatus.Completed, result);

        Output.WriteLine($"Strategy: {config.Review.Strategy ?? "core (default)"}");
        Output.WriteLine($"PostComments: {aporiaOptions.Value.EnablePostComments}");
        Output.WriteLine($"Findings: {result.Findings.Count}  (maxComments: {config.Review.MaxComments})");
        var files = result.Findings.Select(f => f.FilePath).Distinct().ToList();
        Output.WriteLine($"Files: {string.Join(", ", files)}");
        Output.WriteLine($"Sessions: {SessionDirectory}");
    }

    [Fact]
    public async Task Review_FullPipeline_Verbose()
    {
        await ResetReviewState(TestEvent);

        var config = await Git.GetConfig(TestEvent);
        var diff = await Git.GetDiff(TestEvent, config);
        var prContext = await Git.GetPrContext(TestEvent);
        var result = await Reviewer.Review(TestEvent, diff, config, prContext);
        await Git.PostReview(TestEvent, diff, result);

        Output.WriteLine($"Findings: {result.Findings.Count}  (maxComments: {config.Review.MaxComments})\n");

        foreach (var f in result.Findings)
            Output.WriteLine($"  [{f.Severity}] {f.FilePath}:{f.StartLine}-{f.EndLine}  {f.Message[..Math.Min(80, f.Message.Length)]}...");

        Output.WriteLine($"\nSummary:\n{result.Summary}\n");
        Output.WriteLine($"Sessions: {SessionDirectory}\n");

        await PrintThreads(TestEvent);
    }

    [Fact]
    public async Task Review_WithWorkItems_IncludesWorkItemContext()
    {
        await ResetReviewState(TestEvent);

        var prContext = await Git.GetPrContext(TestEvent);

        Output.WriteLine($"Work items: {prContext.WorkItems?.Count ?? 0}");
        if (prContext.WorkItems is { Count: > 0 })
        {
            foreach (var wi in prContext.WorkItems)
            {
                Output.WriteLine($"  [{wi.Type}] {wi.Title}");
                Output.WriteLine($"    Description: {wi.Description?[..Math.Min(100, wi.Description?.Length ?? 0)]}...");
                Output.WriteLine($"    AcceptanceCriteria: {wi.AcceptanceCriteria?[..Math.Min(100, wi.AcceptanceCriteria?.Length ?? 0)]}...");
                if (wi.Parent is not null)
                    Output.WriteLine($"    Parent: [{wi.Parent.Type}] {wi.Parent.Title}");
            }
        }

        Assert.NotNull(prContext.WorkItems);
        Assert.NotEmpty(prContext.WorkItems);
    }

    /// <summary>
    /// If TestTarget:Strategy is set (e.g. via env var TestTarget__Strategy=copilot),
    /// override the strategy from the repo's .aporia.json config.
    /// </summary>
    private ProjectConfig ApplyStrategyOverride(ProjectConfig config)
    {
        var strategyOverride = Services.GetRequiredService<IConfiguration>()
            .GetValue<string>("TestTarget:Strategy");

        if (strategyOverride is null)
            return config;

        Output.WriteLine($"Strategy override: {strategyOverride}");
        return config.WithStrategy(strategyOverride);
    }

    [Fact]
    public async Task SelfReview_FullPipeline()
    {
        var target = Scenarios.SelfReview;
        await ResetReviewState(target);

        var config = await Git.GetConfig(target);
        var diff = await Git.GetDiff(target, config);
        var prContext = await Git.GetPrContext(target);
        var result = await Reviewer.Review(target, diff, config, prContext);

        await Git.PostReview(target, diff, result);

        Output.WriteLine($"Findings: {result.Findings.Count}  (maxComments: {config.Review.MaxComments})");
        var files = result.Findings.Select(f => f.FilePath).Distinct().ToList();
        Output.WriteLine($"Files: {string.Join(", ", files)}");
        Output.WriteLine($"Sessions: {SessionDirectory}");
    }
}
