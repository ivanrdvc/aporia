using System.Text.Json;
using System.Text.Json.Serialization;

using Revu.Tests.Integration.Fixtures;

using Xunit.Abstractions;

namespace Revu.Tests.Integration;

/// <summary>
/// Captures real ADO diff data as eval fixture JSON. Only calls ADO API — no LLM.
/// Run manually, then copy the output JSON to Revu.Tests.Eval/TestData/.
/// </summary>
public class FixtureCaptureTests(
    AppFixture fixture,
    ITestOutputHelper output) : IntegrationTestBase(fixture, output)
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public async Task Capture_CleanPrNoFindings()
    {
        var req = AdoThreadHelper.PrRequest(13, "refs/heads/feature/order-status-endpoint");

        // Reset iteration state so GetDiff returns the full diff
        await ResetReviewState(req);

        var config = await Git.GetConfig(req);
        var diff = await Git.GetDiff(req, config);

        Output.WriteLine($"Diff: {diff.Files.Count} files, cursor {diff.Cursor}");
        foreach (var f in diff.Files)
            Output.WriteLine($"  [{f.Kind}] {f.Path}");

        // Fetch context files the agent might investigate
        var contextPaths = new[]
        {
            "/src/Ordering.API/Apis/OrdersApi.cs",
            "/src/Ordering.API/Apis/OrderServices.cs",
            "/src/Ordering.API/Application/Queries/IOrderQueries.cs",
            "/src/Ordering.API/Application/Queries/OrderQueries.cs",
            "/src/Ordering.API/Application/Queries/OrderStatusResponse.cs"
        };

        var files = new Dictionary<string, string>();
        foreach (var path in contextPaths)
        {
            var content = await Git.GetFile(req, path);
            if (content is not null)
            {
                files[path] = content;
                Output.WriteLine($"  Fetched: {path} ({content.Length} chars)");
            }
            else
            {
                Output.WriteLine($"  Missing: {path}");
            }
        }

        var fixtureData = new
        {
            request = new
            {
                provider = "ado",
                project = req.Project,
                repositoryId = req.RepositoryId,
                repositoryName = req.RepositoryName,
                pullRequestId = req.PullRequestId,
                sourceBranch = req.SourceBranch,
                targetBranch = req.TargetBranch
            },
            config,
            diff = new
            {
                files = diff.Files.Select(f => new
                {
                    path = f.Path,
                    kind = f.Kind.ToString().ToLowerInvariant(),
                    patch = f.Patch,
                    content = f.Content,
                    oldPath = f.OldPath
                })
            },
            files,
            expectations = new
            {
                expectNoFindings = true,
                expectedFindings = Array.Empty<object>()
            }
        };

        var json = JsonSerializer.Serialize(fixtureData, s_jsonOptions);

        // Write to file
        var outputPath = Path.Combine(AppContext.BaseDirectory, "captured-fixture.json");
        await File.WriteAllTextAsync(outputPath, json);
        Output.WriteLine($"\nFixture written to: {outputPath}");
        Output.WriteLine($"JSON length: {json.Length} chars");
    }
}
