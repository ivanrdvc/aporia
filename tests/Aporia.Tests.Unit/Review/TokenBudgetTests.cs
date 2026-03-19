using Aporia.Git;
using Aporia.Review;

namespace Aporia.Tests.Unit.Review;

public class TokenBudgetTests
{
    [Fact]
    public void BuildReviewPrompt_SmallFileShowsDiffAndFullSource()
    {
        var smallSource = "public class Foo { }";
        var diff = new Diff([
            new FileChange("small.cs", ChangeKind.Edit, "+ public class Foo { }", smallSource)
        ]);

        var prompt = Prompts.BuildReviewPrompt(diff);

        // Diff is preserved (shows what changed)
        Assert.Contains("+ public class Foo { }", prompt);
        // Full source is also included (shows full context)
        Assert.Contains("<full-source>", prompt);
        Assert.Contains(smallSource, prompt);
    }

    [Fact]
    public void BuildReviewPrompt_LargeSourceFileShowsDiffOnly()
    {
        // Source content over SmallFileTokenThreshold
        var largeSource = string.Join('\n', Enumerable.Range(0, 1000).Select(i => $"line {i} with padding content here"));
        var diff = new Diff([
            new FileChange("large.cs", ChangeKind.Edit, "+ added", largeSource)
        ]);

        var prompt = Prompts.BuildReviewPrompt(diff);

        Assert.DoesNotContain("<full-source>", prompt);
        Assert.Contains("### large.cs", prompt);
        Assert.Contains("+ added", prompt);
    }
}
