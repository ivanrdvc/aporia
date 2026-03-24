using Microsoft.Extensions.DependencyInjection;

using Aporia.DocWatch;
using Aporia.Git;
using Aporia.Tests.Integration.Fixtures;

using Xunit.Abstractions;

namespace Aporia.Tests.Integration;

public class DocWatchTests(
    AppFixture fixture,
    ITestOutputHelper output) : IntegrationTestBase(fixture, output)
{
    [Fact]
    public async Task DocWatch_EShopPr53_CreatesDocsUpdate()
    {
        var git = Services.GetRequiredKeyedService<IGitConnector>(GitProvider.Ado);
        var publisher = Services.GetRequiredKeyedService<IDocPublisher>(GitProvider.Ado);
        var watcher = Services.GetRequiredService<DocWatcher>();

        var req = new DocWatchRequest(
            Provider: GitProvider.Ado,
            SourceRepo: "ivanradovic__ivanrdvc__a9f4a02d-bc66-4fb6-8009-a8ea2b713e6e",
            SourceRepoName: "eShop",
            PullRequestId: 53,
            SourceBranch: "refs/heads/feature/order-tracking-notifications",
            TargetBranch: "refs/heads/main",
            DocsRepo: "ivanradovic__ivanrdvc__dadd7fd9-ce06-471d-87d4-8c37346d6967",
            Organization: "ivanradovic");

        Output.WriteLine($"Source: eShop PR #53");
        Output.WriteLine($"Docs repo: eShop-docs (dadd7fd9-ce06-471d-87d4-8c37346d6967)");
        Output.WriteLine($"Sessions: {SessionDirectory}");
        Output.WriteLine("");

        var outcome = await watcher.Process(git, publisher, req);

        switch (outcome)
        {
            case DocWatchOutcome.Created c:
                Output.WriteLine($"SUCCESS: Created docs PR #{c.PrNumber}");
                Output.WriteLine($"View at: https://dev.azure.com/ivanradovic/ivanrdvc/_git/eShop-docs/pullrequest/{c.PrNumber}");
                break;
            case DocWatchOutcome.Updated u:
                Output.WriteLine($"SUCCESS: Updated existing docs PR #{u.PrNumber}");
                Output.WriteLine($"View at: https://dev.azure.com/ivanradovic/ivanrdvc/_git/eShop-docs/pullrequest/{u.PrNumber}");
                break;
            default:
                Output.WriteLine("Agent decided no docs update needed (Skipped)");
                break;
        }

        Assert.NotSame(DocWatchOutcome.Skipped, outcome);
    }
}
