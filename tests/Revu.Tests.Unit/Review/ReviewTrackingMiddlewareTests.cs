using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Revu.Git;
using Revu.Infra.Cosmos;
using Revu.Infra.Middleware;

namespace Revu.Tests.Unit.Review;

public class ReviewTrackingMiddlewareTests
{
    private readonly IReviewStore _reviewStore = Substitute.For<IReviewStore>();
    private readonly IRepoStore _repoStore = Substitute.For<IRepoStore>();
    private readonly ReviewTrackingMiddleware _sut = new();

    private readonly ReviewRequest _req = new(
        GitProvider.Ado, "proj", "repo-1", "repo-1", 42, "refs/heads/feature", "refs/heads/main");

    [Fact]
    public async Task Invoke_NonReviewFunction_PassesThrough()
    {
        var context = CreateContext("SomeOtherFunction");
        var called = false;

        await _sut.Invoke(context, _ => { called = true; return Task.CompletedTask; });

        Assert.True(called);
        await _reviewStore.DidNotReceive().SaveAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int?>(),
            Arg.Any<ReviewStatus>(), Arg.Any<int>(), Arg.Any<long>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Invoke_CompletedReview_PersistsEvent()
    {
        var context = CreateContext();

        await _sut.Invoke(context, ctx =>
        {
            ReviewContext.Set(ctx, _req, ReviewStatus.Completed, iterationId: 3, findingsCount: 5);
            return Task.CompletedTask;
        });

        await _reviewStore.Received(1).SaveAsync(
            "repo-1", 42, 3,
            ReviewStatus.Completed, 5, Arg.Any<long>(), Arg.Any<string?>());

        await _repoStore.Received(1).UpdateLastReviewedAsync("repo-1");
    }

    [Fact]
    public async Task Invoke_SkippedReview_PersistsEvent()
    {
        var context = CreateContext();

        await _sut.Invoke(context, ctx =>
        {
            ReviewContext.Set(ctx, _req, ReviewStatus.Skipped, iterationId: 2, findingsCount: 0);
            return Task.CompletedTask;
        });

        await _reviewStore.Received(1).SaveAsync(
            "repo-1", 42, 2,
            ReviewStatus.Skipped, 0, Arg.Any<long>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Invoke_PipelineThrows_RecordsFailedAndRethrows()
    {
        var context = CreateContext();
        context.Items[ReviewContext.RequestKey] = _req;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.Invoke(context, _ => throw new InvalidOperationException("LLM exploded")));

        Assert.Equal("LLM exploded", ex.Message);

        await _reviewStore.Received(1).SaveAsync(
            "repo-1", 42, Arg.Any<int?>(),
            ReviewStatus.Failed, Arg.Any<int>(), Arg.Any<long>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Invoke_PipelineThrows_DoesNotUpdateLastReviewed()
    {
        var context = CreateContext();
        context.Items[ReviewContext.RequestKey] = _req;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.Invoke(context, _ => throw new InvalidOperationException("boom")));

        await _repoStore.DidNotReceive().UpdateLastReviewedAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task Invoke_TrackingFails_DoesNotThrow()
    {
        var context = CreateContext();
        _reviewStore.SaveAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int?>(),
            Arg.Any<ReviewStatus>(), Arg.Any<int>(), Arg.Any<long>(), Arg.Any<string?>())
            .ThrowsAsync(new Exception("Cosmos down"));

        await _sut.Invoke(context, ctx =>
        {
            ReviewContext.Set(ctx, _req, ReviewStatus.Completed, iterationId: 1, findingsCount: 2);
            return Task.CompletedTask;
        });

        // Should not throw — tracking failure is swallowed
    }

    [Fact]
    public async Task Invoke_NoContextSet_SkipsPersistence()
    {
        var context = CreateContext();

        await _sut.Invoke(context, _ => Task.CompletedTask);

        await _reviewStore.DidNotReceive().SaveAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int?>(),
            Arg.Any<ReviewStatus>(), Arg.Any<int>(), Arg.Any<long>(), Arg.Any<string?>());
        await _repoStore.DidNotReceive().UpdateLastReviewedAsync(Arg.Any<string>());
    }

    private FunctionContext CreateContext(string functionName = "ReviewProcessor")
    {
        var context = Substitute.For<FunctionContext>();

        var definition = Substitute.For<FunctionDefinition>();
        definition.Name.Returns(functionName);
        context.FunctionDefinition.Returns(definition);

        context.Items.Returns(new Dictionary<object, object>());

        var bindingContext = Substitute.For<BindingContext>();
        bindingContext.BindingData.Returns(new Dictionary<string, object?>
        {
            ["Project"] = "proj",
            ["RepositoryId"] = "repo-1"
        });
        context.BindingContext.Returns(bindingContext);

        var services = new ServiceCollection();
        services.AddSingleton(_reviewStore);
        services.AddSingleton(_repoStore);
        services.AddSingleton<ILogger<ReviewTrackingMiddleware>>(NullLogger<ReviewTrackingMiddleware>.Instance);
        context.InstanceServices.Returns(services.BuildServiceProvider());

        return context;
    }
}
