using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Revu.Git;
using Revu.Review;

namespace Revu.Tests.Unit.Review;

public class CoreStrategyTests
{
    private readonly IGitConnector _git = Substitute.For<IGitConnector>();

    private readonly ReviewRequest _request = new(GitProvider.Ado, "proj", "repo", "repo", 1, "refs/heads/feature", "refs/heads/main");
    private readonly Diff _diff = new([new FileChange("file.cs", ChangeKind.Edit, "+ added line")]);

    [Fact]
    public async Task Analyze_ValidJsonResponse_ReturnsDeserializedResult()
    {
        var json = """{"findings":[{"filePath":"file.cs","startLine":1,"endLine":1,"severity":"critical","message":"Potential null ref","suggestion":null,"suggestedCode":null}],"summary":"Found one issue."}""";
        var sut = CreateSut(json);

        var result = await sut.Review(_request, _diff, ProjectConfig.Default);

        Assert.Single(result.Findings);
        Assert.Equal("file.cs", result.Findings[0].FilePath);
        Assert.Equal("Found one issue.", result.Summary);
    }

    [Fact]
    public async Task Analyze_InvalidJsonResponse_ReturnsFallback()
    {
        var sut = CreateSut("not json");

        var result = await sut.Review(_request, _diff, ProjectConfig.Default);

        Assert.Empty(result.Findings);
        Assert.Equal("Review completed but failed to parse structured output.", result.Summary);
    }

    [Fact]
    public async Task Analyze_EmptyFindings_ReturnsEmptyList()
    {
        var sut = CreateSut("""{"findings":[],"summary":"LGTM"}""");

        var result = await sut.Review(_request, _diff, ProjectConfig.Default);

        Assert.Empty(result.Findings);
        Assert.Equal("LGTM", result.Summary);
    }

    private CoreStrategy CreateSut(string reviewerResponse)
    {
        var reviewer = Substitute.For<IChatClient>();
        reviewer.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, reviewerResponse)));

        var explorer = Substitute.For<IChatClient>();
        return new(reviewer, explorer, _git, new InMemoryChatHistoryProvider(),
            new FileAgentSkillsProvider(skillPath: Path.Combine(AppContext.BaseDirectory, "Skills")),
            new PrContextProvider(),
            NullLogger<CoreStrategy>.Instance);
    }
}
