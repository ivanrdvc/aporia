using System.Text;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Revu.CodeGraph;
using Revu.Git;
using Revu.Infra;
using Revu.Infra.AI;
using Revu.Infra.Middleware;
using Revu.Infra.Telemetry;

namespace Revu.Review;

public class CoreStrategy(
    [FromKeyedServices(ModelKey.Reasoning)] IChatClient reviewerClient,
    [FromKeyedServices(ModelKey.Default)] IChatClient explorerClient,
    ChatHistoryProvider sessionProvider,
    FileAgentSkillsProvider skillsProvider,
    PrContextProvider prContextProvider,
    ILogger<CoreStrategy> logger) : IReviewStrategy
{
    private const int MaxExplorationsPerReview = 8;
    private const int MaxConcurrentExplorations = 3;
    private const int ExplorerMaxRoundtrips = 10;
    private const int ReviewerMaxRoundtrips = 6;

    public async Task<ReviewResult> Review(ReviewRequest req, Diff diff, ProjectConfig config, IGitConnector git, CodeGraphQuery? codeGraph = null, CancellationToken ct = default)
    {
        var prompt = BuildReviewPrompt(diff);
        var tools = new ReviewerTools(git, req, diff, codeGraph);
        var exploreTool = GuardedExploreTool.Create(explorerClient, tools, sessionProvider, logger);

        var reviewer = reviewerClient
            .AsBuilder()
            .UseFunctionInvocation(null, fic =>
            {
                fic.MaximumIterationsPerRequest = ReviewerMaxRoundtrips;
                fic.AllowConcurrentInvocation = true;
            })
            .Build()
            .AsAIAgent(new ChatClientAgentOptions
            {
                Name = "Reviewer",
                ChatOptions = new ChatOptions
                {
                    Instructions = Prompts.ReviewerInstructions,
                    Tools = [
                        AIFunctionFactory.Create(tools.FetchFile),
                        AIFunctionFactory.Create(tools.ListDirectory),
                        AIFunctionFactory.Create(tools.SearchCode),
                        AIFunctionFactory.Create(tools.QueryCodeGraph),
                        exploreTool,
                    ],
                    AllowMultipleToolCalls = true,
                    ResponseFormat = ChatResponseFormat.ForJsonSchema<ReviewResult>(),
                    Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Medium },
                    AdditionalProperties = new() { ["strict"] = true }
                },
                ChatHistoryProvider = sessionProvider,
                AIContextProviders = [skillsProvider, prContextProvider]
            })
            .AsBuilder()
            .UseOpenTelemetry(sourceName: Telemetry.ServiceName, configure: c => c.EnableSensitiveData = true)
            .Use(runFunc: (msgs, s, o, agent, token) => AgentErrorMiddleware.Handle(msgs, s, o, agent, token, logger),
                runStreamingFunc: null)
            .Build();

        var prContext = await git.GetPrContext(req);

        var session = await reviewer.CreateSessionAsync(cancellationToken: ct);
        session.StateBag.SetValue(SessionKeys.ConversationId, req.ConversationId);
        session.StateBag.SetValue(SessionKeys.PrContext, prContext);
        session.StateBag.SetValue(SessionKeys.ProjectConfig, config);

        var response = await reviewer.RunAsync(prompt, session, cancellationToken: ct);
        return response.ExtractResult<ReviewResult>(logger)
            ?? new([], "Review completed but failed to parse structured output.");
    }

    internal static string BuildReviewPrompt(Diff diff)
    {
        var sb = new StringBuilder();
        const int smallFileTokenThreshold = 1500;

        var decisions = new List<(FileChange file, string level)>();
        foreach (var file in diff.Files)
        {
            if (file.Kind is ChangeKind.Delete)
                decisions.Add((file, "delete"));
            else if (file.Patch is not null && file.Content is not null
                                            && TokenCounter.Count(file.Content) <= smallFileTokenThreshold)
                decisions.Add((file, "full"));
            else if (file.Patch is not null)
                decisions.Add((file, "diff"));
            else if (file.Kind is ChangeKind.Rename)
                decisions.Add((file, "rename"));
        }

        // Emit manifest
        var fullFiles = decisions.Where(d => d.level == "full").ToList();
        var diffFiles = decisions.Where(d => d.level == "diff").ToList();

        sb.AppendLine("## Changed files\n");

        if (fullFiles.Count > 0)
        {
            sb.AppendLine("Full source (do NOT fetch — you have complete context):");
            foreach (var (f, _) in fullFiles)
                sb.AppendLine($"- {f.Path}");
            sb.AppendLine();
        }

        if (diffFiles.Count > 0)
        {
            sb.AppendLine("Diff only (fetch for full file if needed):");
            foreach (var (f, _) in diffFiles)
                sb.AppendLine($"- {f.Path}");
            sb.AppendLine();
        }

        sb.AppendLine("---\n");

        // Emit file sections
        foreach (var (file, level) in decisions)
        {
            switch (level)
            {
                case "full":
                    sb.AppendLine($"### {file.Path}");
                    if (file.Kind is ChangeKind.Rename && file.OldPath is not null)
                        sb.AppendLine($"(renamed from `{file.OldPath}`)");
                    sb.AppendLine(file.Patch);
                    sb.AppendLine($"\n<full-source>\n{file.Content}\n</full-source>");
                    break;

                case "diff":
                    sb.AppendLine($"### {file.Path}");
                    if (file.Kind is ChangeKind.Rename && file.OldPath is not null)
                        sb.AppendLine($"(renamed from `{file.OldPath}`)");
                    sb.AppendLine(file.Patch);
                    break;

                case "rename":
                    sb.AppendLine($"- {file.Path} — renamed from `{file.OldPath}` (no content changes)");
                    break;

                case "delete":
                    sb.AppendLine($"- {file.Path} — deleted");
                    break;
            }
        }

        return sb.ToString();
    }

    private class GuardedExploreTool : DelegatingAIFunction
    {
        private readonly SemaphoreSlim _concurrency = new(MaxConcurrentExplorations);
        private readonly AIAgent _explorer;
        private readonly ILogger _logger;
        private int _explorationCount;

        private GuardedExploreTool(AIFunction inner, AIAgent explorer, ILogger logger) : base(inner)
        {
            _explorer = explorer;
            _logger = logger;
        }

        public override string Description =>
            "Spawn an explorer agent for cross-file analytical work. The explorer reads files, " +
            "searches code, compares patterns, and returns a structured conclusion with file " +
            "references. Use when your question requires reading and comparing multiple files. " +
            "For reading a single file, use FetchFile directly.";

        public static GuardedExploreTool Create(
            IChatClient explorerClient,
            ReviewerTools tools,
            ChatHistoryProvider sessionProvider,
            ILogger logger)
        {
            var explorer = explorerClient
                .AsBuilder()
                .UseFunctionInvocation(null, fic =>
                {
                    fic.MaximumIterationsPerRequest = ExplorerMaxRoundtrips;
                    fic.AllowConcurrentInvocation = true;
                })
                .Build()
                .AsAIAgent(new ChatClientAgentOptions
                {
                    Name = "Explorer",
                    ChatOptions = new ChatOptions
                    {
                        Instructions = Prompts.ExplorerInstructions,
                        Tools =
                        [
                            AIFunctionFactory.Create(tools.FetchFile),
                            AIFunctionFactory.Create(tools.ListDirectory),
                            AIFunctionFactory.Create(tools.SearchCode),
                            AIFunctionFactory.Create(tools.QueryCodeGraph),
                        ],
                        ResponseFormat = ChatResponseFormat.ForJsonSchema<ExplorationResult>(),
                        AdditionalProperties = new() { ["strict"] = true }
                    },
                    ChatHistoryProvider = sessionProvider
                })
                .AsBuilder()
                .UseOpenTelemetry(sourceName: Telemetry.ServiceName, configure: c => c.EnableSensitiveData = true)
                .Build();

            // AsAIFunction provides function metadata for DelegatingAIFunction,
            // but we call RunAsync directly to extract the last message (avoiding
            // AsAIFunction's response.Text which concatenates all intermediate messages).
            var fn = explorer.AsAIFunction(new AIFunctionFactoryOptions { Name = "Explore" });
            return new GuardedExploreTool(fn, explorer, logger);
        }

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var idx = Interlocked.Increment(ref _explorationCount);
            if (idx > MaxExplorationsPerReview)
                return "Exploration budget exhausted. Produce your review from what you have.";

            var query = arguments.TryGetValue("query", out var q) ? q?.ToString() ?? "" : "";
            _logger.LogInformation("Exploration {Index} dispatched: {Query}", idx, query);
            Telemetry.Explorations.Add(1);

            await _concurrency.WaitAsync(cancellationToken);
            try
            {
                var response = await _explorer.RunAsync(query, cancellationToken: cancellationToken);
                var result = response.ExtractResult<ExplorationResult>(_logger);

                if (result is not null)
                {
                    _logger.LogInformation("Exploration {Index} done", idx);
                    return response.Messages.Last().Text!;
                }

                Telemetry.ExplorationFailures.Add(1);
                return $"Exploration failed to produce structured results for: {query}";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exploration {Index} failed", idx);
                Telemetry.ExplorationFailures.Add(1);
                return $"Exploration failed for: {query}";
            }
            finally
            {
                _concurrency.Release();
            }
        }
    }
}
