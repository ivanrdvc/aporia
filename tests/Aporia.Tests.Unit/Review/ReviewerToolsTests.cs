using NSubstitute;

using Aporia.Git;
using Aporia.Review;

namespace Aporia.Tests.Unit.Review;

public class ReviewerToolsTests
{
    private static readonly ReviewRequest Req = new(GitProvider.Ado, "proj", "repo", "repo", 1, "refs/heads/feature", "refs/heads/main");

    private static ReviewerTools CreateTools(IGitConnector git, Diff diff) =>
        new(git, Req, diff);

    [Fact]
    public async Task FetchFile_DuplicatePaths_CallsApiOnce()
    {
        var git = Substitute.For<IGitConnector>();
        git.GetFile(Req, Arg.Any<string>()).Returns("content");
        var diff = new Diff([]);

        var tools = CreateTools(git, diff);
        await tools.FetchFile(["src/a.cs", "src/a.cs"]);

        await git.Received(1).GetFile(Req, "src/a.cs");
    }

    [Fact]
    public async Task FetchFile_DuplicatePathsDifferentCasing_CallsApiOnce()
    {
        var git = Substitute.For<IGitConnector>();
        git.GetFile(Req, Arg.Any<string>()).Returns("content");
        var diff = new Diff([]);

        var tools = CreateTools(git, diff);
        await tools.FetchFile(["src/A.cs", "src/a.cs"]);

        await git.Received(1).GetFile(Req, Arg.Any<string>());
    }

    [Fact]
    public async Task SearchCode_DuplicateQueries_CallsApiOnce()
    {
        var git = Substitute.For<IGitConnector>();
        git.SearchCode(Req, "Foo").Returns([new SearchResult("a.cs", 1, "class Foo")]);
        var diff = new Diff([]);

        var tools = CreateTools(git, diff);
        await tools.SearchCode(["Foo", "Foo"]);

        await git.Received(1).SearchCode(Req, "Foo");
    }

    [Fact]
    public async Task SearchCode_DuplicateQueriesDifferentCasing_CallsApiOnce()
    {
        var git = Substitute.For<IGitConnector>();
        git.SearchCode(Req, Arg.Any<string>()).Returns([new SearchResult("a.cs", 1, "class Foo")]);
        var diff = new Diff([]);

        var tools = CreateTools(git, diff);
        await tools.SearchCode(["Foo", "foo"]);

        await git.Received(1).SearchCode(Req, Arg.Any<string>());
    }
}
