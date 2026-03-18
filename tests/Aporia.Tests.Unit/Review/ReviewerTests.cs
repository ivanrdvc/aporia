using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Aporia.Git;
using Aporia.Infra;
using Aporia.Infra.Cosmos;
using Aporia.Review;

namespace Aporia.Tests.Unit.Review;

public class ReviewerTests
{
    [Fact]
    public async Task Review_CodeGraphDisabled_SkipsGraphLoad()
    {
        var codeGraphStore = Substitute.For<ICodeGraphStore>();
        var strategy = Substitute.For<IReviewStrategy>();
        strategy.Review(Arg.Any<ReviewRequest>(), Arg.Any<Diff>(), Arg.Any<ProjectConfig>(),
                Arg.Any<PrContext>(), Arg.Any<Aporia.CodeGraph.CodeGraphQuery?>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult([], "LGTM"));

        var sut = new Reviewer(_ => strategy, codeGraphStore,
            Options.Create(new AporiaOptions { EnableCodeGraph = false }),
            NullLogger<Reviewer>.Instance,
            Substitute.For<IChatClient>(),
            new InMemoryChatHistoryProvider());

        var req = new ReviewRequest(GitProvider.Ado, "proj", "repo", "repo", 1, "refs/heads/feature", "refs/heads/main");
        await sut.Review(req, new Diff([new FileChange("a.cs", ChangeKind.Edit, "+ x")]), ProjectConfig.Default, new PrContext("Test", null, []));

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

    [Fact]
    public void BuildChatPrompt_WithThreadAnchor_IncludesFileAndLine()
    {
        var ctx = new ChatThreadContext(1, null, "/src/Foo.cs", 42, []);

        var prompt = Reviewer.BuildChatPrompt(ctx);

        Assert.Contains("<thread_anchor>", prompt);
        Assert.Contains("File: /src/Foo.cs", prompt);
        Assert.Contains("Line: 42", prompt);
    }

    [Fact]
    public void BuildChatPrompt_NoAnchor_OmitsFileReference()
    {
        var ctx = new ChatThreadContext(1, null, null, null, ["hello"]);

        var prompt = Reviewer.BuildChatPrompt(ctx);

        Assert.DoesNotContain("File:", prompt);
        Assert.DoesNotContain("Line:", prompt);
    }

    [Fact]
    public void BuildChatPrompt_StopsAtChatMarker()
    {
        var messages = new List<string>
        {
            "Why did you flag this?",
            "<!-- aporia:chat -->\nBecause it's wrong",
            "But I disagree"
        };
        var ctx = new ChatThreadContext(1, null, "/src/Foo.cs", 10, messages);

        var prompt = Reviewer.BuildChatPrompt(ctx);

        Assert.Contains("Why did you flag this?", prompt);
        Assert.DoesNotContain("Because it's wrong", prompt);
        Assert.DoesNotContain("But I disagree", prompt);
    }

    [Fact]
    public void BuildChatPrompt_NoMessages_OmitsThreadMessages()
    {
        var ctx = new ChatThreadContext(1, null, "/src/Foo.cs", 10, []);

        var prompt = Reviewer.BuildChatPrompt(ctx);

        // The prompt contains <thread_conversation> in the instructions text,
        // but should NOT contain actual user messages
        var afterInstructions = prompt[(prompt.LastIndexOf("</guidelines>") + "</guidelines>".Length)..];
        Assert.DoesNotContain("thread_conversation>\n", afterInstructions);
    }

    [Fact]
    public void BuildChatPrompt_AllMessagesAreChatReplies_OmitsThreadMessages()
    {
        var messages = new List<string> { "<!-- aporia:chat -->\nFirst reply" };
        var ctx = new ChatThreadContext(1, null, null, null, messages);

        var prompt = Reviewer.BuildChatPrompt(ctx);

        Assert.DoesNotContain("First reply", prompt);
    }
}
