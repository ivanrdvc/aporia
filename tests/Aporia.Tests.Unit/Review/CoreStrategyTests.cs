using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Aporia.Git;
using Aporia.Review;

namespace Aporia.Tests.Unit.Review;

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

        var result = await sut.Review(_request, _diff, ProjectConfig.Default, new PrContext("Test", null, []));

        Assert.Single(result.Findings);
        Assert.Equal("file.cs", result.Findings[0].FilePath);
        Assert.Equal("Found one issue.", result.Summary);
    }

    [Fact]
    public async Task Analyze_InvalidJsonResponse_ReturnsFallback()
    {
        var sut = CreateSut("not json");

        var result = await sut.Review(_request, _diff, ProjectConfig.Default, new PrContext("Test", null, []));

        Assert.Empty(result.Findings);
        Assert.Equal("Review completed but failed to parse structured output.", result.Summary);
    }

    [Fact]
    public async Task Analyze_EmptyFindings_ReturnsEmptyList()
    {
        var sut = CreateSut("""{"findings":[],"summary":"LGTM"}""");

        var result = await sut.Review(_request, _diff, ProjectConfig.Default, new PrContext("Test", null, []));

        Assert.Empty(result.Findings);
        Assert.Equal("LGTM", result.Summary);
    }

    [Fact]
    public void AnnotatePatchWithLineNumbers_AddsCorrectLineNumbers()
    {
        var patch = "@@ -91,5 +91,37 @@ FROM ordering.orders\n" +
                    " \n" +
                    "             return [];\n" +
                    "         }\n" +
                    "+\n" +
                    "+        private async Task<bool> Check(int id)\n" +
                    "+        {\n" +
                    "+            var cred = \"postgres\";";

        var result = Prompts.AnnotatePatchWithLineNumbers(patch);

        Assert.Contains("   91  ", result);
        Assert.Contains("   94 +", result);
        Assert.Contains("   97 +            var cred", result);
    }

    [Fact]
    public void AnnotatePatchWithLineNumbers_DeletedLinesGetNoNumber()
    {
        var patch = "@@ -1,3 +1,3 @@\n context\n-old line\n+new line";

        var result = Prompts.AnnotatePatchWithLineNumbers(patch);

        Assert.Contains("    1  context", result);
        Assert.Contains("      -old line", result);
        Assert.Contains("    2 +new line", result);
    }

    private CoreStrategy CreateSut(string reviewerResponse)
    {
        var reviewer = Substitute.For<IChatClient>();
        reviewer.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, reviewerResponse)));

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IGitConnector>(GitProvider.Ado, _git);
        var sp = services.BuildServiceProvider();

        var explorer = Substitute.For<IChatClient>();
        return new(reviewer, explorer, sp,
            new InMemoryChatHistoryProvider(),
            new FileAgentSkillsProvider(skillPath: Path.Combine(AppContext.BaseDirectory, "Skills")),
            new PrContextProvider(),
            NullLogger<CoreStrategy>.Instance);
    }
}
