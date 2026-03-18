using NSubstitute;

using Aporia.Infra.Cosmos;

namespace Aporia.Tests.Unit.Git;

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
        _store.GetAsync("repo-1", 1).Returns(new PrState { Cursor = "5" });

        var result = await _store.GetAsync("repo-1", 1);

        Assert.NotNull(result);
        Assert.Equal("5", result.Cursor);
    }

    [Fact]
    public async Task SaveAsync_ThenGet_ReturnsLatestState()
    {
        _store.GetAsync("repo-1", 1).Returns(new PrState { Cursor = "7" });

        await _store.SaveAsync("repo-1", 1, "7");
        var result = await _store.GetAsync("repo-1", 1);

        Assert.Equal("7", result!.Cursor);
    }

    [Fact]
    public async Task DifferentPRs_IndependentState()
    {
        _store.GetAsync("repo-1", 1).Returns(new PrState { Cursor = "3" });
        _store.GetAsync("repo-1", 2).Returns(new PrState { Cursor = "5" });

        Assert.Equal("3", (await _store.GetAsync("repo-1", 1))!.Cursor);
        Assert.Equal("5", (await _store.GetAsync("repo-1", 2))!.Cursor);
    }

    [Fact]
    public async Task DifferentRepos_IndependentState()
    {
        _store.GetAsync("repo-1", 1).Returns(new PrState { Cursor = "3" });
        _store.GetAsync("repo-2", 1).Returns(new PrState { Cursor = "5" });

        Assert.Equal("3", (await _store.GetAsync("repo-1", 1))!.Cursor);
        Assert.Equal("5", (await _store.GetAsync("repo-2", 1))!.Cursor);
    }
}
