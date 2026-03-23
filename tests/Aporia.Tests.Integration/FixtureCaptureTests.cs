using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Aporia.Tests.Integration.Fixtures;

using Xunit.Abstractions;

namespace Aporia.Tests.Integration;

/// <summary>
/// Captures real diff data as eval fixture JSON. Only calls Git API — no LLM.
/// Run manually, then copy the output JSON to Aporia.Tests.Eval/TestData/.
/// Uses TestTarget from appsettings.test.json for the PR to capture.
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

    private ReviewRequest GetTestEvent()
    {
        var config = Services.GetRequiredService<IConfiguration>();
        var prId = config.GetValue<int>("TestTarget:PrId");
        var branch = config.GetValue<string>("TestTarget:Branch")!;
        return TestHelper.BuildRequest(prId, branch);
    }

    [Fact]
    public async Task Capture_Fixture()
    {
        var req = GetTestEvent();

        // Reset iteration state so GetDiff returns the full diff
        await ResetReviewState(req);

        var config = await Git.GetConfig(req);
        var diff = await Git.GetDiff(req, config);

        Output.WriteLine($"Diff: {diff.Files.Count} files, cursor {diff.Cursor}");
        foreach (var f in diff.Files)
            Output.WriteLine($"  [{f.Kind}] {f.Path}");

        // Fetch all files in the diff as context the agent might investigate
        var files = new Dictionary<string, string>();
        foreach (var f in diff.Files)
        {
            var content = await Git.GetFile(req, f.Path);
            if (content is not null)
            {
                files[f.Path] = content;
                Output.WriteLine($"  Fetched: {f.Path} ({content.Length} chars)");
            }
            else
            {
                Output.WriteLine($"  Missing: {f.Path}");
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
