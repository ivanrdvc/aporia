using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Aporia.CodeGraph;
using Aporia.Git;
using Aporia.Infra;
using Aporia.Infra.AI;
using Aporia.Infra.Middleware;
using Aporia.Infra.Telemetry;

namespace Aporia.Review;

public class CoreStrategy(
    [FromKeyedServices(ModelKey.Reasoning)] IChatClient reviewerClient,
    [FromKeyedServices(ModelKey.Default)] IChatClient explorerClient,
    IServiceProvider sp,
    ChatHistoryProvider sessionProvider,
    FileAgentSkillsProvider skillsProvider,
    PrContextProvider prContextProvider,
    ILogger<CoreStrategy> logger) : IReviewStrategy
{
    private const int MaxExplorationsPerReview = 8;
    private const int MaxConcurrentExplorations = 3;
    private const int ExplorerMaxRoundtrips = 10;
    private const int ReviewerMaxRoundtrips = 6;

    public async Task<ReviewResult> Review(ReviewRequest req, Diff diff, ProjectConfig config, PrContext prContext, CodeGraphQuery? codeGraph = null, CancellationToken ct = default)
    {
        var git = sp.GetRequiredKeyedService<IGitConnector>(req.Provider);
        var prompt = Prompts.BuildReviewPrompt(diff);
        var tools = new ReviewerTools(git, req, diff, codeGraph);
        var exploreTool = GuardedExploreTool.Create(explorerClient, tools, codeGraph, sessionProvider, logger);

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
                    Tools = BuildTools(tools, exploreTool, codeGraph),
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

        var session = await reviewer.CreateSessionAsync(cancellationToken: ct);
        session.StateBag.SetValue(SessionKeys.ConversationId, req.ConversationId);
        session.StateBag.SetValue(SessionKeys.PrContext, prContext);
        session.StateBag.SetValue(SessionKeys.ProjectConfig, config);

        var response = await reviewer.RunAsync(prompt, session, cancellationToken: ct);
        return response.ExtractResult<ReviewResult>(logger)
            ?? new([], "Review completed but failed to parse structured output.");
    }

    private static List<AITool> BuildTools(ReviewerTools tools, AITool? exploreTool = null, CodeGraphQuery? codeGraph = null)
    {
        var list = new List<AITool>
        {
            AIFunctionFactory.Create(tools.FetchFile),
            AIFunctionFactory.Create(tools.ListDirectory),
            AIFunctionFactory.Create(tools.SearchCode),
        };
        if (codeGraph is not null)
            list.Add(AIFunctionFactory.Create(tools.QueryCodeGraph));
        if (exploreTool is not null)
            list.Add(exploreTool);
        return list;
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
            CodeGraphQuery? codeGraph,
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
                        Tools = BuildTools(tools, codeGraph: codeGraph),
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
