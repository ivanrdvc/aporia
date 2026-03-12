using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Revu.Tests.Integration.Fixtures;

using Xunit.Abstractions;

namespace Revu.Tests.Integration;

public class ReviewTests(
    AppFixture fixture,
    ITestOutputHelper output) : IntegrationTestBase(fixture, output)
{
    private ReviewRequest TestEvent => GetTestEvent();

    private ReviewRequest GetTestEvent()
    {
        var config = fixture.Services.GetRequiredService<IConfiguration>();
        var prId = config.GetValue<int>("TestTarget:PrId");
        var branch = config.GetValue<string>("TestTarget:Branch")!;
        return AdoThreadHelper.PrRequest(prId, branch);
    }

    [Fact]
    public async Task Review_FullPipeline_PostsFindings()
    {
        await ResetReviewState(TestEvent);
        await GitClient.CleanThreads(TestEvent);

        var config = await Git.GetConfig(TestEvent);
        var diff = await Git.GetDiff(TestEvent, config);
        var result = await Reviewer.Review(TestEvent, diff, config);

        await Git.PostReview(TestEvent, diff, result);

        Output.WriteLine($"Findings: {result.Findings.Count}  (maxComments: {config.Review.MaxComments})");
        var files = result.Findings.Select(f => f.FilePath).Distinct().ToList();
        Output.WriteLine($"Files: {string.Join(", ", files)}");
        Output.WriteLine($"Sessions: {SessionDirectory}");
    }

    [Fact]
    public async Task Review_FullPipeline_Verbose()
    {
        await ResetReviewState(TestEvent);
        await GitClient.CleanThreads(TestEvent);

        var config = await Git.GetConfig(TestEvent);
        var diff = await Git.GetDiff(TestEvent, config);
        var result = await Reviewer.Review(TestEvent, diff, config);
        await Git.PostReview(TestEvent, diff, result);

        Output.WriteLine($"Findings: {result.Findings.Count}  (maxComments: {config.Review.MaxComments})\n");

        foreach (var f in result.Findings)
            Output.WriteLine($"  [{f.Severity}] {f.FilePath}:{f.StartLine}-{f.EndLine}  {f.Message[..Math.Min(80, f.Message.Length)]}...");

        Output.WriteLine($"\nSummary:\n{result.Summary}\n");
        Output.WriteLine($"Sessions: {SessionDirectory}\n");

        await PrintThreads(TestEvent);
    }
}
