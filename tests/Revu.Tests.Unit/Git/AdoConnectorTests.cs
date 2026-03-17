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

        var a = Finding.Fingerprint(finding);
        var b = Finding.Fingerprint(finding);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Fingerprint_DifferentLines_ReturnsSameHash()
    {
        var a = new Finding("src/Foo.cs", 10, 15, Severity.Critical, "Null ref here");
        var b = new Finding("src/Foo.cs", 20, 25, Severity.Critical, "Null ref here");

        Assert.Equal(Finding.Fingerprint(a), Finding.Fingerprint(b));
    }

    [Fact]
    public void Fingerprint_DifferentSeverity_ReturnsSameHash()
    {
        var a = new Finding("src/Foo.cs", 10, 15, Severity.Critical, "Null ref here");
        var b = new Finding("src/Foo.cs", 10, 15, Severity.Info, "Null ref here");

        Assert.Equal(Finding.Fingerprint(a), Finding.Fingerprint(b));
    }

    [Fact]
    public void Fingerprint_DifferentMessage_ReturnsDifferentHash()
    {
        var a = new Finding("src/Foo.cs", 10, 15, Severity.Critical, "Null ref here");
        var b = new Finding("src/Foo.cs", 10, 15, Severity.Critical, "Race condition");

        Assert.NotEqual(Finding.Fingerprint(a), Finding.Fingerprint(b));
    }

    [Fact]
    public void Fingerprint_DifferentFile_ReturnsDifferentHash()
    {
        var a = new Finding("src/Foo.cs", 10, 15, Severity.Critical, "Null ref here");
        var b = new Finding("src/Bar.cs", 10, 15, Severity.Critical, "Null ref here");

        Assert.NotEqual(Finding.Fingerprint(a), Finding.Fingerprint(b));
    }

    [Fact]
    public void Fingerprint_CaseInsensitive()
    {
        var a = new Finding("src/Foo.cs", 10, 15, Severity.Critical, "Null Ref Here");
        var b = new Finding("SRC/FOO.CS", 10, 15, Severity.Critical, "null ref here");

        Assert.Equal(Finding.Fingerprint(a), Finding.Fingerprint(b));
    }

    [Fact]
    public void Fingerprint_LeadingSlashIgnored()
    {
        var a = new Finding("/src/Foo.cs", 10, 15, Severity.Critical, "Null ref");
        var b = new Finding("src/Foo.cs", 10, 15, Severity.Critical, "Null ref");

        Assert.Equal(Finding.Fingerprint(a), Finding.Fingerprint(b));
    }

    [Fact]
    public void Fingerprint_WhitespaceTrimmed()
    {
        var a = new Finding("src/Foo.cs", 10, 15, Severity.Critical, "  Null ref  ");
        var b = new Finding("src/Foo.cs", 10, 15, Severity.Critical, "Null ref");

        Assert.Equal(Finding.Fingerprint(a), Finding.Fingerprint(b));
    }

    [Fact]
    public async Task GetDiff_NullCursor_ReturnsEmptyDiff()
    {
        var git = Substitute.For<GitHttpClient>(new Uri("https://test"), new VssCredentials());
        git.GetPullRequestIterationsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), null, null, default)
            .Returns([new GitPullRequestIteration { Id = null }]);

        var diff = await CreateConnector(git, Substitute.For<IPrStateStore>()).GetDiff(Req, ProjectConfig.Default);

        Assert.Empty(diff.Files);
        Assert.Null(diff.Cursor);
    }

    [Theory]
    [InlineData(false, 3, 5)] // non-incremental, prior state exists → skip
    [InlineData(true, 5, 5)]  // incremental, same iteration → skip
    [InlineData(true, 6, 5)]  // incremental, state ahead → skip
    public async Task GetDiff_AlreadyReviewed_ReturnsEmptyWithCursor(bool incremental, int stateIter, int currentIter)
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
            Cursor = stateIter.ToString()
        });

        var diff = await CreateConnector(git, store, incremental).GetDiff(Req, ProjectConfig.Default);

        Assert.Empty(diff.Files);
        Assert.Equal(currentIter.ToString(), diff.Cursor);
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
                Cursor = "5"
            });

        git.GetPullRequestIterationChangesAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), null, default)
            .Returns(new GitPullRequestIterationChanges { ChangeEntries = [], NextTop = 0, NextSkip = 0 });

        var diff = await CreateConnector(git, store, incremental: true).GetDiff(Req, ProjectConfig.Default);

        Assert.Equal("6", diff.Cursor);
    }

    [Fact]
    public void CleanHtml_Null_ReturnsNull()
    {
        Assert.Null(AdoWorkItemMapper.CleanHtml(null));
    }

    [Fact]
    public void CleanHtml_Empty_ReturnsNull()
    {
        Assert.Null(AdoWorkItemMapper.CleanHtml(""));
        Assert.Null(AdoWorkItemMapper.CleanHtml("   "));
    }

    [Fact]
    public void CleanHtml_StripsTags()
    {
        var result = AdoWorkItemMapper.CleanHtml("<p>Hello <b>world</b></p>");

        Assert.NotNull(result);
        Assert.DoesNotContain("<", result);
        Assert.Contains("Hello", result);
        Assert.Contains("world", result);
    }

    [Fact]
    public void CleanHtml_ListItems_ConvertedToBullets()
    {
        var result = AdoWorkItemMapper.CleanHtml("<ul><li>First</li><li>Second</li></ul>");

        Assert.NotNull(result);
        Assert.Contains("- First", result);
        Assert.Contains("- Second", result);
    }

    [Fact]
    public void CleanHtml_DecodesEntities()
    {
        var result = AdoWorkItemMapper.CleanHtml("<p>a &amp; b &lt; c</p>");

        Assert.NotNull(result);
        Assert.Contains("a & b < c", result);
    }

    [Fact]
    public void CleanHtml_TruncatesLongContent()
    {
        var longHtml = $"<p>{new string('x', 2000)}</p>";
        var result = AdoWorkItemMapper.CleanHtml(longHtml);

        Assert.NotNull(result);
        Assert.True(result.Length <= 1512); // 1500 + " [truncated]"
        Assert.EndsWith("[truncated]", result);
    }

    [Fact]
    public void CleanHtml_ShortContent_NotTruncated()
    {
        var result = AdoWorkItemMapper.CleanHtml("<p>Short content</p>");

        Assert.NotNull(result);
        Assert.DoesNotContain("[truncated]", result);
    }

    private static readonly ReviewRequest Req = new(
        GitProvider.Ado, "proj", "repo-id", "repo", 42, "refs/heads/feature", "refs/heads/main", "testorg");

    private static AdoConnector CreateConnector(GitHttpClient git, IPrStateStore store, bool incremental = false)
    {
        var connector = new AdoConnector(
            store,
            Options.Create(new AdoOptions
            {
                Organizations = new Dictionary<string, AdoOrgConfig>
                {
                    ["testorg"] = new() { Organization = "testorg", PersonalAccessToken = "fake-pat" }
                }
            }),
            Options.Create(new RevuOptions { EnableIncrementalReviews = incremental }),
            Substitute.For<ILogger<AdoConnector>>());

        connector._gitClients["testorg"] = git;
        return connector;
    }
}
