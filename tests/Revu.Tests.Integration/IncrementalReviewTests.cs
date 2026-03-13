using Revu.Git;
using Revu.Tests.Integration.Fixtures;

using Xunit.Abstractions;

namespace Revu.Tests.Integration;

/// Verifies the incremental review pipeline: a second run with no new commits
/// returns an empty diff and skips review, and duplicate findings are not reposted.
public class IncrementalReviewTests(
    AppFixture fixture,
    ITestOutputHelper output) : IntegrationTestBase(fixture, output)
{
    private static readonly ReviewRequest TestEvent = Scenarios.IncrementalTest;

    [Fact]
    public async Task IncrementalReview_SkipsWhenNoNewIteration()
    {
        // --- Clean slate ---
        await GitClient.CleanThreads(TestEvent);
        Output.WriteLine("=== Clean slate ===\n");

        // --- Run 1: full review ---
        var config = await Git.GetConfig(TestEvent);
        var diff1 = await Git.GetDiff(TestEvent, config);

        Output.WriteLine($"Run 1 diff: {diff1.Files.Count} files, cursor {diff1.Cursor}");
        Assert.NotEmpty(diff1.Files);
        Assert.NotNull(diff1.Cursor);

        var result1 = await Reviewer.Review(TestEvent, diff1, config);
        await Git.PostReview(TestEvent, diff1, result1);

        var threads1 = await GitClient.GetRevuThreads(TestEvent);
        var inlineThreads1 = threads1.Where(t => t.ThreadContext?.FilePath is not null).ToList();

        Output.WriteLine($"Run 1: {result1.Findings.Count} inline findings, {inlineThreads1.Count} threads posted");
        foreach (var t in inlineThreads1)
            Output.WriteLine($"  [{t.Status}] {t.ThreadContext!.FilePath} L{t.ThreadContext.RightFileStart?.Line}");
        Output.WriteLine("");

        // --- Run 2: same PR, no new commits → GetDiff should return empty ---
        var diff2 = await Git.GetDiff(TestEvent, config);

        Output.WriteLine($"Run 2 diff: {diff2.Files.Count} files, cursor {diff2.Cursor}");
        Assert.Empty(diff2.Files);

        // Verify no new threads were created
        var threads2 = await GitClient.GetRevuThreads(TestEvent);
        var inlineThreads2 = threads2.Where(t => t.ThreadContext?.FilePath is not null).ToList();

        Output.WriteLine($"Run 2: skipped review (no new changes). Threads unchanged: {inlineThreads2.Count}");
        Assert.Equal(inlineThreads1.Count, inlineThreads2.Count);

        Output.WriteLine("\n=== Incremental skip test passed ===");
    }

    [Fact]
    public async Task PostReview_DeduplicatesMatchingFingerprints()
    {
        // --- Clean slate ---
        await GitClient.CleanThreads(TestEvent);

        // Use a synthetic result — we're testing PostReview dedup, not the LLM
        var diff = new Diff([new FileChange("src/Basket.API/Model/BasketItem.cs", ChangeKind.Edit, "+ added")]);
        var finding = new Finding("src/Basket.API/Model/BasketItem.cs", 13, 17, Severity.Critical,
            "DiscountPercent has no validation — values > 100 make GetTotal() negative.");
        var result = new ReviewResult([finding], "Test summary");

        // --- Post once ---
        await Git.PostReview(TestEvent, diff, result);

        var threadsAfterFirst = await GitClient.GetRevuThreads(TestEvent);
        var inlineAfterFirst = threadsAfterFirst.Count(t => t.ThreadContext?.FilePath is not null);
        Output.WriteLine($"After first post: {inlineAfterFirst} inline threads");

        // --- Post the SAME result again — should not create new threads ---
        await Git.PostReview(TestEvent, diff, result);

        var threadsAfterSecond = await GitClient.GetRevuThreads(TestEvent);
        var inlineAfterSecond = threadsAfterSecond.Count(t => t.ThreadContext?.FilePath is not null);
        Output.WriteLine($"After second post (same findings): {inlineAfterSecond} inline threads");

        // Same findings → same fingerprints → no new threads
        Assert.Equal(inlineAfterFirst, inlineAfterSecond);

        Output.WriteLine("=== Dedup test passed ===");
    }
}
