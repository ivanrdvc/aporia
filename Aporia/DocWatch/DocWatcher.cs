using System.Text.Json;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Aporia.Git;
using Aporia.Infra.AI;
using Aporia.Infra.Telemetry;
using Aporia.Review;

namespace Aporia.DocWatch;

/// <summary>
/// Orchestrates the doc watch pipeline: runs the LLM agent to analyze changes,
/// pushes file updates, and creates or updates the persistent documentation PR.
/// </summary>
public class DocWatcher(
    [FromKeyedServices(ModelKey.Reasoning)] IChatClient reasoningClient,
    ChatHistoryProvider sessionProvider,
    ILogger<DocWatcher> logger)
{
    private const int MaxRoundtrips = 8;

    public async Task<DocWatchOutcome> Process(
        IGitConnector git,
        IDocPublisher publisher,
        DocWatchRequest req,
        CancellationToken ct = default)
    {
        var sourceReq = ToSourceRequest(req);
        var docsReq = ToDocsRequest(req);

        var config = await git.GetConfig(sourceReq);
        var diff = await git.GetDiff(sourceReq, config);

        if (diff.Files.Count == 0)
            return DocWatchOutcome.Skipped;

        var prContextTask = git.GetPrContext(sourceReq);
        var docsConfigTask = git.GetConfig(docsReq);
        var existingPrTask = publisher.FindOpenPullRequest(req.DocsRepo, DocWatchConstants.Label, req.InstallationId);

        await Task.WhenAll(prContextTask, docsConfigTask, existingPrTask);

        var prContext = await prContextTask;
        var docsConfig = await docsConfigTask;
        var existingPr = await existingPrTask;

        var tools = BuildTools(git, sourceReq, docsReq, diff);
        var prompt = BuildPrompt(req, diff, prContext, docsConfig, existingPr?.Body);
        var result = await RunAgent(prompt, tools, ct);

        if (!result.ShouldUpdate)
            return DocWatchOutcome.Skipped;

        var sourceRepoReadable = req.SourceRepo.Replace("__", "/");
        var branch = existingPr?.Branch ?? DocWatchConstants.BranchName;

        if (existingPr is null)
            await publisher.CreateBranch(req.DocsRepo, "main", branch, req.InstallationId);

        var allFiles = result.FilesToCreate.Concat(result.FilesToUpdate).Concat(result.Diagrams)
            .GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();
        await publisher.PushFiles(req.DocsRepo, branch, allFiles,
            $"docs: update from {sourceRepoReadable}#{req.PullRequestId}", req.InstallationId);

        var changelogEntry = $"- **{sourceRepoReadable}#{req.PullRequestId}** — {result.ChangelogEntry}";

        if (existingPr is { } existing)
        {
            var updatedBody = AppendChangelog(existing.Body, changelogEntry);
            await publisher.UpdatePullRequest(req.DocsRepo, existing.Number, body: updatedBody, installationId: req.InstallationId);

            await publisher.AddComment(req.SourceRepo, req.PullRequestId,
                $"Documentation updated in {req.DocsRepo.Replace("__", "/")}#{existing.Number}",
                req.InstallationId);

            return new DocWatchOutcome.Updated(existing.Number);
        }
        else
        {
            var prBody = FormatPrBody(changelogEntry);
            var prNumber = await publisher.CreatePullRequest(req.DocsRepo, branch, "main",
                $"{DocWatchConstants.TitlePrefix} Documentation updates", prBody, req.InstallationId);

            await publisher.AddComment(req.SourceRepo, req.PullRequestId,
                $"Documentation PR opened: {req.DocsRepo.Replace("__", "/")}#{prNumber}",
                req.InstallationId);

            return new DocWatchOutcome.Created(prNumber);
        }
    }

    private async Task<DocWatchResult> RunAgent(string prompt, IList<AITool> tools, CancellationToken ct)
    {
        var agent = reasoningClient
            .AsBuilder()
            .UseFunctionInvocation(null, fic =>
            {
                fic.MaximumIterationsPerRequest = MaxRoundtrips;
                fic.AllowConcurrentInvocation = true;
            })
            .Build()
            .AsAIAgent(new ChatClientAgentOptions
            {
                Name = "DocWatch",
                ChatOptions = new ChatOptions
                {
                    Instructions = Instructions,
                    Tools = tools,
                    AllowMultipleToolCalls = true,
                    ResponseFormat = ChatResponseFormat.ForJsonSchema<DocWatchResult>(),
                    Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Medium },
                    AdditionalProperties = new() { ["strict"] = true }
                },
                ChatHistoryProvider = sessionProvider
            })
            .AsBuilder()
            .UseOpenTelemetry(sourceName: Telemetry.ServiceName, configure: c => c.EnableSensitiveData = true)
            .Build();

        var session = await agent.CreateSessionAsync(cancellationToken: ct);

        try
        {
            var response = await agent.RunAsync<DocWatchResult>(prompt, session, cancellationToken: ct);
            return response.Result;
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            logger.LogWarning(ex, "Doc watch agent failed to produce structured output");
            return new DocWatchResult(false, [], [], [], "");
        }
    }

    private static List<AITool> BuildTools(IGitConnector git, ReviewRequest sourceReq, ReviewRequest docsReq, Diff diff)
    {
        var sourceTools = new ReviewerTools(git, sourceReq, diff);
        var docsTools = new ReviewerTools(git, docsReq, new Diff([]));

        return
        [
            AIFunctionFactory.Create(
                (string[] paths) => sourceTools.FetchFile(paths),
                "FetchSourceFile",
                "Read files from the source repo that was changed."),
            AIFunctionFactory.Create(
                (string path) => sourceTools.ListDirectory(path),
                "ListSourceDirectory",
                "List directory contents in the source repo."),
            AIFunctionFactory.Create(
                (string[] queries) => sourceTools.SearchCode(queries),
                "SearchSourceCode",
                "Search code in the source repo."),
            AIFunctionFactory.Create(
                (string[] paths) => docsTools.FetchFile(paths),
                "FetchDocsFile",
                "Read files from the documentation repo."),
            AIFunctionFactory.Create(
                (string path) => docsTools.ListDirectory(path),
                "ListDocsDirectory",
                "List directory contents in the documentation repo."),
            AIFunctionFactory.Create(
                (string[] queries) => docsTools.SearchCode(queries),
                "SearchDocsCode",
                "Search content in the documentation repo."),
        ];
    }

    private static string BuildPrompt(
        DocWatchRequest req,
        Diff diff,
        PrContext prContext,
        ProjectConfig docsConfig,
        string? existingPrBody)
    {
        var sourceRepo = req.SourceRepo.Replace("__", "/");
        var filesChanged = string.Join("\n", diff.Files.Select(f => $"  {f.Kind}: {f.Path}"));

        var prompt = $"""
            A pull request was merged in **{sourceRepo}** (PR #{req.PullRequestId}).

            **PR Title:** {prContext.Title}
            **PR Description:** {prContext.Description ?? "(none)"}
            **Target Branch:** {req.TargetBranch}

            **Commit Messages:**
            {string.Join("\n", prContext.CommitMessages.Select(m => $"  - {m}"))}

            **Files Changed:**
            {filesChanged}
            """;

        if (docsConfig.DocWatch?.Context is { } context)
            prompt += $"\n\n**Documentation Context:** {context}";

        if (docsConfig.DocWatch?.Diagrams is { Enabled: true } diagrams)
            prompt += $"\n\n**Diagrams:** Enabled, format: {diagrams.Format}. Generate C4-style diagrams (Excalidraw JSON) when the change is architectural.";

        if (existingPrBody is not null)
            prompt += $"\n\n**Existing Doc Watch PR Body (already processed changes):**\n{existingPrBody}\n\nDo not duplicate work already covered. Only add new content from this PR.";

        return prompt;
    }

    private static ReviewRequest ToSourceRequest(DocWatchRequest req) => new(
        Provider: req.Provider,
        Project: ParseProject(req.Provider, req.SourceRepo, req.Organization),
        RepositoryId: ParseRepoId(req.Provider, req.SourceRepo),
        RepositoryName: req.SourceRepoName,
        PullRequestId: req.PullRequestId,
        SourceBranch: req.SourceBranch,
        TargetBranch: req.TargetBranch,
        Organization: req.Organization,
        InstallationId: req.InstallationId);

    private static ReviewRequest ToDocsRequest(DocWatchRequest req) => new(
        Provider: req.Provider,
        Project: ParseProject(req.Provider, req.DocsRepo, req.Organization),
        RepositoryId: ParseRepoId(req.Provider, req.DocsRepo),
        RepositoryName: req.DocsRepo.Split("__").Last(),
        PullRequestId: 0,
        SourceBranch: "main",
        TargetBranch: "main",
        Organization: req.Organization,
        InstallationId: req.InstallationId);

    /// <summary>
    /// ADO repo IDs are "org__project__repoGuid" — project is the second segment.
    /// GitHub repo IDs are "owner__repo" — organization serves as both owner and project.
    /// </summary>
    private static string ParseProject(GitProvider provider, string repoId, string fallback)
    {
        var parts = repoId.Split("__");
        return provider == GitProvider.Ado && parts.Length >= 3 ? parts[1] : fallback;
    }

    private static string ParseRepoId(GitProvider provider, string repoId)
    {
        var parts = repoId.Split("__");
        return provider == GitProvider.Ado && parts.Length >= 3 ? parts[2] : repoId;
    }

    private static string FormatPrBody(string initialChangelog) =>
        $"""
        ## Documentation Updates

        Automatically generated documentation updates from watched source repos.

        ### Changes
        {initialChangelog}

        ---
        Generated by Aporia Doc Watch
        """;

    private static string AppendChangelog(string existingBody, string newEntry)
    {
        const string marker = "### Changes";
        var idx = existingBody.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
            return existingBody + $"\n\n{marker}\n{newEntry}";

        var insertAt = idx + marker.Length;
        return existingBody.Insert(insertAt, $"\n{newEntry}");
    }

    private const string Instructions = """
        You are a documentation agent. Your job is to determine whether a code change in a source
        repository warrants updating documentation in a separate documentation repository.

        ## Your Process

        1. **Read the diff and PR context** to understand what changed and why.
        2. **Explore the documentation repo** using ListDocsDirectory and FetchDocsFile to understand
           its structure, what topics are covered, and the writing style/conventions.
        3. **Judge relevance** — is this change documentation-worthy? Not every code change needs docs.
           Focus on: architectural changes, new components/services, API surface changes, data model
           changes, new integrations, breaking changes, significant behavior changes.
        4. **If relevant**, generate the documentation content. Match the existing style and structure
           of the docs repo. Place files where they logically belong based on the existing organization.
        5. **If not relevant**, return ShouldUpdate = false.

        ## Documentation Style

        - Match the existing tone, format, and conventions of the docs repo.
        - Be concise and factual. Document the what and why, not implementation details.
        - Use the source repo's code and PR context to write accurate descriptions.
        - If updating an existing doc, preserve its structure and only modify relevant sections.

        ## Diagrams

        When diagrams are enabled and the change is architectural (new component, changed data flow,
        new service integration), generate Excalidraw JSON files with simple C4-style diagrams:
        - Use rectangles for components/services
        - Use arrows for data flow / dependencies
        - Keep layouts simple and readable
        - Use consistent colors: blue for new components, gray for existing

        ## Output

        Return a DocWatchResult with:
        - ShouldUpdate: whether docs need updating
        - FilesToCreate: new files to add (path relative to repo root + full content)
        - FilesToUpdate: existing files to modify (path + full updated content)
        - Diagrams: Excalidraw JSON files (path + content)
        - ChangelogEntry: one-line summary of what this source PR contributes to the docs
        """;
}

/// <summary>Result of a doc watch pipeline run.</summary>
public abstract record DocWatchOutcome
{
    public static readonly DocWatchOutcome Skipped = new SkippedOutcome();
    public record Created(int PrNumber) : DocWatchOutcome;
    public record Updated(int PrNumber) : DocWatchOutcome;
    private record SkippedOutcome : DocWatchOutcome;
}

public record DocWatchResult(
    bool ShouldUpdate,
    List<DocFile> FilesToCreate,
    List<DocFile> FilesToUpdate,
    List<DocFile> Diagrams,
    string ChangelogEntry
);

public record DocFile(string Path, string Content);
