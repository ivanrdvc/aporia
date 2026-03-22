using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Aporia.CodeGraph;
using Aporia.Functions;
using Aporia.Git;
using Aporia.Infra;
using Aporia.Infra.Cosmos;
using Aporia.Review;

namespace Aporia.Tests.Unit.Functions;

public class ReviewFunctionTests
{
    private readonly IGitConnector _git = Substitute.For<IGitConnector>();
    private readonly ICodeGraphStore _codeGraphStore = Substitute.For<ICodeGraphStore>();
    private readonly IReviewStore _reviewStore = Substitute.For<IReviewStore>();
    private readonly IReviewStrategy _strategy = Substitute.For<IReviewStrategy>();

    private readonly ReviewRequest _req = new(
        GitProvider.Ado, "proj", "repo-1", "repo-1", 42, "refs/heads/feature", "refs/heads/main");

    private ReviewFunction CreateSut(AporiaOptions? aporiaOptions = null)
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton(GitProvider.Ado, _git);
        var sp = services.BuildServiceProvider();
        var opts = aporiaOptions ?? new AporiaOptions();

        return new(
            sp,
            new Reviewer(_ => _strategy, _codeGraphStore, Options.Create(new AporiaOptions { EnableCodeGraph = true }), NullLogger<Reviewer>.Instance, Substitute.For<IChatClient>(), new InMemoryChatHistoryProvider()),
            _reviewStore,
            Options.Create(opts),
            NullLogger<ReviewFunction>.Instance);
    }

    [Fact]
    public async Task Run_CompletedReview_SavesEvent()
    {
        var diff = new Diff([new FileChange("a.cs", ChangeKind.Edit, "+ x")], "3");
        var result = new ReviewResult([new Finding("a.cs", 1, null, Severity.Warning, "msg")], "summary");

        _git.GetConfig(_req).Returns(ProjectConfig.Default);
        _git.GetDiff(_req, ProjectConfig.Default).Returns(diff);
        _git.GetPrContext(_req).Returns(new PrContext("Test PR", null, []));
        _strategy.Review(Arg.Any<ReviewRequest>(), Arg.Any<Diff>(), Arg.Any<ProjectConfig>(), Arg.Any<PrContext>(), Arg.Any<CodeGraphQuery?>(), Arg.Any<CancellationToken>())
            .Returns(result);

        await CreateSut().Run(_req);

        await _reviewStore.Received(1).SaveAsync(
            _req, Arg.Any<Diff>(), ReviewStatus.Completed, Arg.Any<ReviewResult?>());
    }

    [Fact]
    public async Task Run_EmptyDiff_SavesSkippedStatus()
    {
        var diff = new Diff([], "2");
        _git.GetConfig(_req).Returns(ProjectConfig.Default);
        _git.GetDiff(_req, ProjectConfig.Default).Returns(diff);

        await CreateSut().Run(_req);

        await _reviewStore.Received(1).SaveAsync(
            _req, Arg.Any<Diff>(), ReviewStatus.Skipped, Arg.Any<ReviewResult?>());
    }

    [Fact]
    public async Task Run_PostCommentsDisabled_SkipsPostReview()
    {
        var diff = new Diff([new FileChange("a.cs", ChangeKind.Edit, "+ x")], "3");
        var result = new ReviewResult([new Finding("a.cs", 1, null, Severity.Warning, "msg")], "summary");

        _git.GetConfig(_req).Returns(ProjectConfig.Default);
        _git.GetDiff(_req, ProjectConfig.Default).Returns(diff);
        _git.GetPrContext(_req).Returns(new PrContext("Test PR", null, []));
        _strategy.Review(Arg.Any<ReviewRequest>(), Arg.Any<Diff>(), Arg.Any<ProjectConfig>(), Arg.Any<PrContext>(), Arg.Any<CodeGraphQuery?>(), Arg.Any<CancellationToken>())
            .Returns(result);

        await CreateSut(new AporiaOptions { EnablePostComments = false }).Run(_req);

        await _git.DidNotReceive().PostReview(Arg.Any<ReviewRequest>(), Arg.Any<Diff>(), Arg.Any<ReviewResult>());
        await _reviewStore.Received(1).SaveAsync(_req, Arg.Any<Diff>(), ReviewStatus.Completed, Arg.Any<ReviewResult?>());
    }

    [Fact]
    public async Task Run_EmptyDiff_DoesNotReview()
    {
        var diff = new Diff([], "2");
        _git.GetConfig(_req).Returns(ProjectConfig.Default);
        _git.GetDiff(_req, ProjectConfig.Default).Returns(diff);

        await CreateSut().Run(_req);

        await _strategy.DidNotReceive().Review(
            Arg.Any<ReviewRequest>(), Arg.Any<Diff>(), Arg.Any<ProjectConfig>(), Arg.Any<PrContext>(), Arg.Any<CodeGraphQuery?>(), Arg.Any<CancellationToken>());
        await _git.DidNotReceive().PostReview(Arg.Any<ReviewRequest>(), Arg.Any<Diff>(), Arg.Any<ReviewResult>());
    }
}
