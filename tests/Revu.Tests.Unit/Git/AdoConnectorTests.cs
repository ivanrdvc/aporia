using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.Common;

using NSubstitute;

using Revu.Git;
using Revu.Infra;
using Revu.Infra.Cosmos;

namespace Revu.Tests.Unit.Git;

public class AdoConnectorTests
{
    [Fact]
    public void Fingerprint_SameFinding_ReturnsSameHash()
    {
        var finding = new Finding("src/Foo.cs", 10, 15, Severity.Critical, "Null ref here");

        var a = AdoConnector.Fingerprint(finding);
        var b = AdoConnector.Fingerprint(finding);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Fingerprint_DifferentLines_ReturnsSameHash()
    {
        var a = new Finding("src/Foo.cs", 10, 15, Severity.Critical, "Null ref here");
        var b = new Finding("src/Foo.cs", 20, 25, Severity.Critical, "Null ref here");

        Assert.Equal(AdoConnector.Fingerprint(a), AdoConnector.Fingerprint(b));
    }

    [Fact]
    public void Fingerprint_DifferentSeverity_ReturnsSameHash()
    {
        var a = new Finding("src/Foo.cs", 10, 15, Severity.Critical, "Null ref here");
        var b = new Finding("src/Foo.cs", 10, 15, Severity.Info, "Null ref here");

        Assert.Equal(AdoConnector.Fingerprint(a), AdoConnector.Fingerprint(b));
    }

    [Fact]
    public void Fingerprint_DifferentMessage_ReturnsDifferentHash()
    {
        var a = new Finding("src/Foo.cs", 10, 15, Severity.Critical, "Null ref here");
        var b = new Finding("src/Foo.cs", 10, 15, Severity.Critical, "Race condition");

        Assert.NotEqual(AdoConnector.Fingerprint(a), AdoConnector.Fingerprint(b));
    }

    [Fact]
    public void Fingerprint_DifferentFile_ReturnsDifferentHash()
    {
        var a = new Finding("src/Foo.cs", 10, 15, Severity.Critical, "Null ref here");
        var b = new Finding("src/Bar.cs", 10, 15, Severity.Critical, "Null ref here");

        Assert.NotEqual(AdoConnector.Fingerprint(a), AdoConnector.Fingerprint(b));
    }

    [Fact]
    public void Fingerprint_CaseInsensitive()
    {
        var a = new Finding("src/Foo.cs", 10, 15, Severity.Critical, "Null Ref Here");
        var b = new Finding("SRC/FOO.CS", 10, 15, Severity.Critical, "null ref here");

        Assert.Equal(AdoConnector.Fingerprint(a), AdoConnector.Fingerprint(b));
    }

    [Fact]
    public void Fingerprint_LeadingSlashIgnored()
    {
        var a = new Finding("/src/Foo.cs", 10, 15, Severity.Critical, "Null ref");
        var b = new Finding("src/Foo.cs", 10, 15, Severity.Critical, "Null ref");

        Assert.Equal(AdoConnector.Fingerprint(a), AdoConnector.Fingerprint(b));
    }

    [Fact]
    public void Fingerprint_WhitespaceTrimmed()
    {
        var a = new Finding("src/Foo.cs", 10, 15, Severity.Critical, "  Null ref  ");
        var b = new Finding("src/Foo.cs", 10, 15, Severity.Critical, "Null ref");

        Assert.Equal(AdoConnector.Fingerprint(a), AdoConnector.Fingerprint(b));
    }

    [Fact]
    public async Task GetDiff_NullIterationId_ReturnsEmptyDiff()
    {
        var git = Substitute.For<GitHttpClient>(new Uri("https://test"), new VssCredentials());
        git.GetPullRequestIterationsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), null, null, default)
            .Returns([new GitPullRequestIteration { Id = null }]);

        var diff = await CreateConnector(git, Substitute.For<IPrStateStore>()).GetDiff(Req, ProjectConfig.Default);

        Assert.Empty(diff.Files);
        Assert.Null(diff.IterationId);
    }

    [Theory]
    [InlineData(false, 3, 5)] // non-incremental, prior state exists → skip
    [InlineData(true, 5, 5)]  // incremental, same iteration → skip
    [InlineData(true, 6, 5)]  // incremental, state ahead → skip
    public async Task GetDiff_AlreadyReviewed_ReturnsEmptyWithIterationId(bool incremental, int stateIter, int currentIter)
    {
        var git = Substitute.For<GitHttpClient>(new Uri("https://test"), new VssCredentials());
        git.GetPullRequestIterationsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), null, null, default)
            .Returns([new GitPullRequestIteration { Id = currentIter }]);

        var store = Substitute.For<IPrStateStore>();
        store.GetAsync("repo-id", 42).Returns(new PrState
        {
            Id = "x",
            RepositoryId = "repo-id",
            PullRequestId = 42,
            IterationId = stateIter
        });

        var diff = await CreateConnector(git, store, incremental).GetDiff(Req, ProjectConfig.Default);

        Assert.Empty(diff.Files);
        Assert.Equal(currentIter, diff.IterationId);
    }

    [Theory]
    [InlineData(true)]  // incremental, newer iteration → proceed
    [InlineData(false)] // no state at all → proceed
    public async Task GetDiff_NotYetReviewed_ProceedsToFetchChanges(bool hasState)
    {
        var git = Substitute.For<GitHttpClient>(new Uri("https://test"), new VssCredentials());
        git.GetPullRequestIterationsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), null, null, default)
            .Returns([new GitPullRequestIteration
            {
                Id = 6,
                SourceRefCommit = new GitCommitRef { CommitId = "abc" },
                TargetRefCommit = new GitCommitRef { CommitId = "def" }
            }]);

        var store = Substitute.For<IPrStateStore>();
        if (hasState)
            store.GetAsync("repo-id", 42).Returns(new PrState
            {
                Id = "x",
                RepositoryId = "repo-id",
                PullRequestId = 42,
                IterationId = 5
            });

        git.GetPullRequestIterationChangesAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), null, default)
            .Returns(new GitPullRequestIterationChanges { ChangeEntries = [], NextTop = 0, NextSkip = 0 });

        var diff = await CreateConnector(git, store, incremental: true).GetDiff(Req, ProjectConfig.Default);

        Assert.Equal(6, diff.IterationId);
    }

    private static readonly ReviewRequest Req = new(
        GitProvider.Ado, "proj", "repo-id", "repo", 42, "refs/heads/feature", "refs/heads/main");

    private static AdoConnector CreateConnector(GitHttpClient git, IPrStateStore store, bool incremental = false) =>
        new(git, store, Substitute.For<IHttpClientFactory>(),
            Options.Create(new RevuOptions { IncrementalReviews = incremental }),
            Substitute.For<ILogger<AdoConnector>>());
}
