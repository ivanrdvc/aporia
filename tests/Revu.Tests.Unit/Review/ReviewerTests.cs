using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Revu.Git;
using Revu.Infra;
using Revu.Infra.Cosmos;
using Revu.Review;

namespace Revu.Tests.Unit.Review;

public class ReviewerTests
{
    [Fact]
    public async Task Review_CodeGraphDisabled_SkipsGraphLoad()
    {
        var codeGraphStore = Substitute.For<ICodeGraphStore>();
        var strategy = Substitute.For<IReviewStrategy>();
        strategy.Review(Arg.Any<ReviewRequest>(), Arg.Any<Diff>(), Arg.Any<ProjectConfig>(),
                Arg.Any<IGitConnector>(), Arg.Any<Revu.CodeGraph.CodeGraphQuery?>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult([], "LGTM"));

        var sut = new Reviewer(_ => strategy, codeGraphStore,
            Options.Create(new RevuOptions { EnableCodeGraph = false }),
            NullLogger<Reviewer>.Instance);

        var git = Substitute.For<IGitConnector>();
        var req = new ReviewRequest(GitProvider.Ado, "proj", "repo", "repo", 1, "refs/heads/feature", "refs/heads/main");
        await sut.Review(req, new Diff([new FileChange("a.cs", ChangeKind.Edit, "+ x")]), ProjectConfig.Default, git);

        await codeGraphStore.DidNotReceive().GetAllAsync(Arg.Any<string>());
    }

    [Theory]
    [InlineData("src/Foo.cs", "src/Foo.cs")]
    [InlineData("/src/Foo.cs", "src/Foo.cs")]
    [InlineData("///src/Foo.cs", "src/Foo.cs")]
    public void NormalizePath_LeadingSlashes_Stripped(string input, string expected)
    {
        Assert.Equal(expected, Reviewer.NormalizePath(input));
    }

    [Theory]
    [InlineData("a/src/Foo.cs", "src/Foo.cs")]
    [InlineData("b/src/Foo.cs", "src/Foo.cs")]
    [InlineData("U/src/Foo.cs", "src/Foo.cs")]
    public void NormalizePath_GitDiffPrefix_Stripped(string input, string expected)
    {
        Assert.Equal(expected, Reviewer.NormalizePath(input));
    }

    [Fact]
    public void NormalizePath_Spaces_Removed()
    {
        Assert.Equal("src/Foo.cs", Reviewer.NormalizePath(" src / Foo.cs "));
    }

    [Fact]
    public void NormalizePath_ShortPath_NotTruncated()
    {
        Assert.Equal("a", Reviewer.NormalizePath("a"));
    }

    [Fact]
    public void NormalizePath_TwoCharPath_NotTruncated()
    {
        Assert.Equal("ab", Reviewer.NormalizePath("ab"));
    }
}
