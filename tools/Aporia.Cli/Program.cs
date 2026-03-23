using System.Text.RegularExpressions;

using Microsoft.Agents.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Aporia;
using Aporia.Cli;
using Aporia.CodeGraph;
using Aporia.Git;
using Aporia.Infra;
using Aporia.Infra.AI;
using Aporia.Infra.Cosmos;
using Aporia.Review;

// ---------------------------------------------------------------------------
// Parse arguments
// ---------------------------------------------------------------------------

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    PrintUsage();
    return 0;
}

string? prUrl = null;
bool post = false;
bool clean = false;
string? modelOverride = null;
string? strategyOverride = null;
bool verbose = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "review":
            break;
        case "--post":
            post = true;
            break;
        case "--clean":
            clean = true;
            break;
        case "--model" when i + 1 < args.Length:
            modelOverride = args[++i];
            break;
        case "--strategy" when i + 1 < args.Length:
            strategyOverride = args[++i];
            break;
        case "--verbose":
            verbose = true;
            break;
        default:
            if (args[i].StartsWith("http"))
                prUrl = args[i];
            else
            {
                Console.Error.WriteLine($"Unknown argument: {args[i]}");
                PrintUsage();
                return 1;
            }
            break;
    }
}

if (prUrl is null)
{
    Console.Error.WriteLine("Error: PR URL is required.");
    PrintUsage();
    return 1;
}

var parsed = PrUrlParser.Parse(prUrl);
if (parsed is null)
{
    Console.Error.WriteLine($"Error: Could not parse PR URL: {prUrl}");
    Console.Error.WriteLine("Supported formats:");
    Console.Error.WriteLine("  https://github.com/owner/repo/pull/123");
    Console.Error.WriteLine("  https://dev.azure.com/org/project/_git/repo/pullrequest/42");
    return 1;
}

// ---------------------------------------------------------------------------
// Build host — reuses Aporia DI without Azure Functions / Cosmos / queues
// ---------------------------------------------------------------------------

var sessionDir = Path.Combine(Directory.GetCurrentDirectory(), "sessions", $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss}");

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    EnvironmentName = Environments.Development,
});

builder.Configuration
    .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
    .AddUserSecrets<AporiaOptions>(optional: true)
    .AddEnvironmentVariables();

if (modelOverride is not null)
{
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Ai:Models:reasoning"] = modelOverride,
    });
}

builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Services.AddOptions<AporiaOptions>().BindConfiguration(AporiaOptions.SectionName);
builder.Services.AddChatClients(builder.Configuration);

if (parsed.Provider is GitProvider.GitHub)
    builder.Services.AddGitHub();
else
    builder.Services.AddAdo();

builder.Services.AddSingleton<PrContextProvider>();
builder.Services.AddSingleton(new FileAgentSkillsProvider(
    skillPath: Path.Combine(AppContext.BaseDirectory, "Skills")));
builder.Services.AddKeyedScoped<IReviewStrategy, CoreStrategy>(ReviewStrategy.Core);
builder.Services.AddKeyedScoped<IReviewStrategy, CopilotStrategy>(ReviewStrategy.Copilot);
builder.Services.AddScoped<Func<string, IReviewStrategy>>(sp =>
    key => sp.GetRequiredKeyedService<IReviewStrategy>(key));
builder.Services.AddScoped<Reviewer>();
builder.Services.AddSingleton<ICodeGraphStore, NullCodeGraphStore>();
builder.Services.AddSingleton<IPrStateStore, NullPrStateStore>();
builder.Services.AddSingleton<ChatHistoryProvider>(new FileSessionProvider(sessionDir));
builder.Services.AddCliTelemetry(builder.Configuration);

using var host = builder.Build();
await host.StartAsync();

// ---------------------------------------------------------------------------
// Resolve PR metadata and run review
// ---------------------------------------------------------------------------

using var scope = host.Services.CreateScope();
var git = scope.ServiceProvider.GetRequiredKeyedService<IGitConnector>(parsed.Provider);

if (clean)
{
    Console.Write($"Abandoning PR #{parsed.PullRequestId} and creating new PR... ");
    var newPrId = await PrRecreator.RecreateAsync(parsed);
    Console.WriteLine($"done. New PR #{newPrId}");
    parsed = parsed with { PullRequestId = newPrId };
}

Console.WriteLine($"Reviewing PR #{parsed.PullRequestId}: {parsed.Owner}/{parsed.RepoName}");

ReviewRequest req;
try
{
    req = await parsed.ToReviewRequest(git);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error resolving PR metadata: {ex.Message}");
    return 1;
}

Console.WriteLine($"  {req.SourceBranch} -> {req.TargetBranch}");

var reviewer = scope.ServiceProvider.GetRequiredService<Reviewer>();
var config = await git.GetConfig(req);

if (strategyOverride is not null)
    config = config.WithStrategy(strategyOverride);

Console.WriteLine($"  Strategy: {config.Review.Strategy ?? "core"} | MaxComments: {config.Review.MaxComments ?? 5}");
Console.WriteLine();

var diffTask = git.GetDiff(req, config);
var prContextTask = git.GetPrContext(req);

var diff = await diffTask;
if (diff.Files.Count == 0)
{
    Console.WriteLine("No files changed — nothing to review.");
    await host.StopAsync();
    return 0;
}

Console.WriteLine($"Files in diff: {diff.Files.Count}");
foreach (var file in diff.Files)
    Console.WriteLine($"  [{file.Kind}] {file.Path}");
Console.WriteLine();

var prContext = await prContextTask;
var result = await reviewer.Review(req, diff, config, prContext);

// ---------------------------------------------------------------------------
// Output
// ---------------------------------------------------------------------------

if (result.Findings.Count == 0)
{
    Console.WriteLine("No issues found.");
}
else
{
    Console.WriteLine($"{result.Findings.Count} finding(s):");
    Console.WriteLine();

    foreach (var f in result.Findings)
    {
        var severity = f.Severity switch
        {
            Severity.Critical => "CRITICAL",
            Severity.Warning => "WARNING",
            Severity.Info => "INFO",
            _ => f.Severity.ToString()
        };

        var lineRange = f.EndLine is not null && f.EndLine != f.StartLine
            ? $"{f.StartLine}-{f.EndLine}"
            : f.StartLine.ToString();

        Console.WriteLine($"  [{severity}] {f.FilePath}:{lineRange}");

        if (verbose)
        {
            Console.WriteLine($"           {f.Message}");
            if (f.CodeFix is not null)
            {
                Console.WriteLine("           Fix:");
                foreach (var line in f.CodeFix.Split('\n'))
                    Console.WriteLine($"             {line}");
            }
            Console.WriteLine();
        }
        else
        {
            var msg = f.Message;
            var dot = msg.IndexOf(". ", StringComparison.Ordinal);
            if (dot > 0 && dot < 120)
                msg = msg[..(dot + 1)];
            else if (msg.Length > 120)
                msg = msg[..120] + "...";
            Console.WriteLine($"           {msg}");
        }
    }
}

Console.WriteLine();

if (verbose)
{
    Console.WriteLine("--- Summary ---");
    Console.WriteLine(result.Summary);
    Console.WriteLine();
}

if (post)
{
    Console.Write("Posting comments to PR... ");
    await git.PostReview(req, diff, result);
    Console.WriteLine("done.");
}
else
{
    Console.WriteLine("Dry run — use --post to post comments to the PR.");
}

Console.WriteLine($"Session saved to {sessionDir}");
await host.StopAsync();
return 0;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

static void PrintUsage()
{
    Console.WriteLine("""
        Usage: aporia review <pr-url> [options]

        Arguments:
          <pr-url>    GitHub or Azure DevOps pull request URL

        Options:
          --post            Post comments to the PR (dry-run by default)
          --clean           Delete previous Aporia comments before posting (requires --post)
          --model <model>   Override reasoning model (e.g. anthropic/claude-sonnet-4-5)
          --strategy <key>  Override review strategy (core, copilot)
          --verbose         Print full findings and summary
          -h, --help        Show this help

        Examples:
          aporia review https://github.com/owner/repo/pull/123
          aporia review https://dev.azure.com/org/project/_git/repo/pullrequest/42 --post
          aporia review https://github.com/owner/repo/pull/123 --model anthropic/claude-sonnet-4-5 --verbose
        """);
}

// ---------------------------------------------------------------------------
// PR URL parser
// ---------------------------------------------------------------------------

internal static partial class PrUrlParser
{
    [GeneratedRegex(@"github\.com/(?<owner>[^/]+)/(?<repo>[^/]+)/pull/(?<pr>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubPattern();

    [GeneratedRegex(@"dev\.azure\.com/(?<org>[^/]+)/(?<project>[^/]+)/_git/(?<repo>[^/]+)/pullrequest/(?<pr>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex AdoPattern();

    public static ParsedPrUrl? Parse(string url)
    {
        var gh = GitHubPattern().Match(url);
        if (gh.Success)
        {
            return new ParsedPrUrl(
                GitProvider.GitHub,
                gh.Groups["owner"].Value,
                gh.Groups["repo"].Value,
                int.Parse(gh.Groups["pr"].Value));
        }

        var ado = AdoPattern().Match(url);
        if (ado.Success)
        {
            return new ParsedPrUrl(
                GitProvider.Ado,
                ado.Groups["org"].Value,
                ado.Groups["repo"].Value,
                int.Parse(ado.Groups["pr"].Value),
                ado.Groups["project"].Value);
        }

        return null;
    }
}

internal record ParsedPrUrl(
    GitProvider Provider,
    string Owner,
    string RepoName,
    int PullRequestId,
    string? Project = null)
{
    public async Task<ReviewRequest> ToReviewRequest(IGitConnector git)
    {
        var repoId = Provider is GitProvider.GitHub
            ? $"{Owner}__{RepoName}"
            : RepoName;

        var stubReq = new ReviewRequest(Provider, Project ?? Owner, repoId, RepoName, PullRequestId, "", "", Owner);
        var branches = await git.GetPrBranches(stubReq)
                       ?? throw new InvalidOperationException($"Could not fetch PR #{PullRequestId} branches.");
        return stubReq with { SourceBranch = branches.Source, TargetBranch = branches.Target };
    }
}

// ---------------------------------------------------------------------------
// No-op stores — CLI doesn't use Cosmos
// ---------------------------------------------------------------------------

internal sealed class NullCodeGraphStore : ICodeGraphStore
{
    public Task<List<FileIndex>> GetAllAsync(string repoId) => Task.FromResult(new List<FileIndex>());
    public Task UpsertFileAsync(FileIndex file) => Task.CompletedTask;
    public Task DeleteOrphansAsync(string repoId, HashSet<string> indexedPaths, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class NullPrStateStore : IPrStateStore
{
    public Task<PrState?> GetAsync(string repositoryId, int pullRequestId) => Task.FromResult<PrState?>(null);
    public Task SaveAsync(string repositoryId, int pullRequestId, string cursor) => Task.CompletedTask;
}
