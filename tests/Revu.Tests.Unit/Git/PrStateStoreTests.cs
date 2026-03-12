using NSubstitute;

using Revu.Infra.Cosmos;

namespace Revu.Tests.Unit.Git;

public class PrStateStoreTests
{
    private readonly IPrStateStore _store = Substitute.For<IPrStateStore>();

    [Fact]
    public async Task GetAsync_NoState_ReturnsNull()
    {
        _store.GetAsync("repo-1", 1).Returns((PrState?)null);

        var result = await _store.GetAsync("repo-1", 1);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAsync_ExistingState_ReturnsState()
    {
        _store.GetAsync("repo-1", 1).Returns(new PrState { IterationId = 5 });

        var result = await _store.GetAsync("repo-1", 1);

        Assert.NotNull(result);
        Assert.Equal(5, result.IterationId);
    }

    [Fact]
    public async Task SaveAsync_ThenGet_ReturnsLatestState()
    {
        _store.GetAsync("repo-1", 1).Returns(new PrState { IterationId = 7 });

        await _store.SaveAsync("repo-1", 1, 7);
        var result = await _store.GetAsync("repo-1", 1);

        Assert.Equal(7, result!.IterationId);
    }

    [Fact]
    public async Task DifferentPRs_IndependentState()
    {
        _store.GetAsync("repo-1", 1).Returns(new PrState { IterationId = 3 });
        _store.GetAsync("repo-1", 2).Returns(new PrState { IterationId = 5 });

        Assert.Equal(3, (await _store.GetAsync("repo-1", 1))!.IterationId);
        Assert.Equal(5, (await _store.GetAsync("repo-1", 2))!.IterationId);
    }

    [Fact]
    public async Task DifferentRepos_IndependentState()
    {
        _store.GetAsync("repo-1", 1).Returns(new PrState { IterationId = 3 });
        _store.GetAsync("repo-2", 1).Returns(new PrState { IterationId = 5 });

        Assert.Equal(3, (await _store.GetAsync("repo-1", 1))!.IterationId);
        Assert.Equal(5, (await _store.GetAsync("repo-2", 1))!.IterationId);
    }
}
