using System.Diagnostics;
using System.Text;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Revu.CodeGraph;
using Revu.Git;
using Revu.Infra;
using Revu.Infra.AI;
using Revu.Infra.Cosmos;
using Revu.Infra.Middleware;
using Revu.Infra.Telemetry;

namespace Revu.Review;

public class Reviewer(
    Func<string, IReviewStrategy> strategyFactory,
    ICodeGraphStore codeGraphStore,
    IOptions<RevuOptions> options,
    ILogger<Reviewer> logger,
    IChatClient chatClient,
    ChatHistoryProvider sessionProvider)
{
    private const int DefaultMaxComments = 5;
    private const int MaxInlineLineSpan = 20;
    private const int MaxCodeFixLineSpan = 15;
    private const int ChatMaxRoundtrips = 3;
    private const string ChatMarker = "<!-- revu:chat -->";

    public async Task<ReviewResult> Review(ReviewRequest req, Diff diff, ProjectConfig config, PrContext prContext, CancellationToken ct = default)
    {
        var tags = new TagList
        {
            { "project", req.Project },
            { "repository", req.RepositoryId }
        };

        var graphDocs = options.Value.EnableCodeGraph ? await codeGraphStore.GetAllAsync(req.RepositoryId) : null;
        var codeGraph = graphDocs is { Count: > 0 } ? new CodeGraphQuery(graphDocs) : null;

        var strategy = strategyFactory(config.Review.Strategy ?? ReviewStrategy.Core);
        var result = await strategy.Review(req, diff, config, prContext, codeGraph, ct);

        // Only allow findings on files actually changed in this PR — context files fetched by
        // investigators are read-only reference material and can't be anchored in ADO.
        var diffPaths = new HashSet<string>(
            diff.Files.Select(f => NormalizePath(f.Path)),
            StringComparer.OrdinalIgnoreCase);

        var inDiff = result.Findings
            .Where(f => diffPaths.Contains(NormalizePath(f.FilePath)))
            .ToList();

        var maxComments = config.Review.MaxComments ?? DefaultMaxComments;

        var picked = inDiff
            .OrderBy(f => f.Severity)
            .Take(maxComments)
            .ToHashSet();

        var inline = picked
            .Select(f => f with { FilePath = "/" + NormalizePath(f.FilePath) })
            .Select(f => f.CodeFix is not null && (f.EndLine ?? f.StartLine) - f.StartLine > MaxCodeFixLineSpan
                ? f with { CodeFix = null }
                : f)
            .Select(f => f.EndLine - f.StartLine > MaxInlineLineSpan ? f with { EndLine = f.StartLine } : f)
            .ToList();

        var summaryFindings = inDiff.Where(f => !picked.Contains(f)).ToList();

        var summary = BuildSummary(result.Summary, summaryFindings);

        if (inDiff.Count == 0)
            summary += "\n\nRevu reviewed the changes and found no issues.";

        logger.LogInformation("Findings: {Total} total, {Inline} inline, {Summary} summary, max {Max}",
            inDiff.Count, inline.Count, summaryFindings.Count, maxComments);

        Telemetry.FindingsGenerated.Add(inDiff.Count, tags);
        Telemetry.FindingsPosted.Add(inline.Count, tags);

        return new ReviewResult(inline, summary);
    }

    /// <summary>
    /// Strip leading slashes, whitespace, and stray characters that models sometimes
    /// inject into file paths in structured output (e.g. "/ src /Catalog .API/..." or "U/src/...").
    /// </summary>
    internal static string NormalizePath(string path)
    {
        path = path.Replace(" ", "");
        // Strip git diff prefix (a/, b/) or hallucinated single-char prefix (U/)
        if (path.Length > 2 && char.IsLetter(path[0]) && path[1] == '/')
            path = path[2..];
        return path.TrimStart('/');
    }

    public async Task<string> Chat(ReviewRequest req, IGitConnector git, ReviewSnapshot? snapshot, ChatThreadContext threadContext, string userMessage, CancellationToken ct = default)
    {
        var tools = new ReviewerTools(git, req, new Diff([]));
        var systemPrompt = BuildChatPrompt(snapshot, threadContext);

        var agent = chatClient
            .AsBuilder()
            .UseFunctionInvocation(null, fic =>
            {
                fic.MaximumIterationsPerRequest = ChatMaxRoundtrips;
                fic.AllowConcurrentInvocation = true;
            })
            .Build()
            .AsAIAgent(new ChatClientAgentOptions
            {
                Name = "ChatAgent",
                ChatOptions = new ChatOptions
                {
                    Instructions = systemPrompt,
                    Tools =
                    [
                        AIFunctionFactory.Create(tools.FetchFile),
                        AIFunctionFactory.Create(tools.ListDirectory),
                        AIFunctionFactory.Create(tools.SearchCode),
                        AIFunctionFactory.Create(tools.QueryCodeGraph),
                    ],
                    AllowMultipleToolCalls = true
                },
                ChatHistoryProvider = sessionProvider
            })
            .AsBuilder()
            .UseOpenTelemetry(sourceName: Telemetry.ServiceName, configure: c => c.EnableSensitiveData = true)
            .Use(runFunc: (msgs, s, o, agent, token) => AgentErrorMiddleware.Handle(msgs, s, o, agent, token, logger),
                runStreamingFunc: null)
            .Build();

        var session = await agent.CreateSessionAsync(cancellationToken: ct);
        session.StateBag.SetValue(SessionKeys.ConversationId, $"chat-{req.RepositoryId}-{req.PullRequestId}");

        var response = await agent.RunAsync(userMessage, session, cancellationToken: ct);
        return response.Messages.LastOrDefault()?.Text
            ?? "I wasn't able to generate a response. Please try again.";
    }

    internal static string BuildChatPrompt(ReviewSnapshot? snapshot, ChatThreadContext threadContext)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Prompts.ChatInstructions);

        if (snapshot is not null)
        {
            sb.AppendLine("\n<review_snapshot>");
            sb.AppendLine($"PR Title: {snapshot.PrTitle}");
            if (!string.IsNullOrWhiteSpace(snapshot.PrDescription))
                sb.AppendLine($"PR Description: {snapshot.PrDescription}");

            sb.AppendLine($"\nReview Summary:\n{snapshot.Result.Summary}");

            if (snapshot.Result.Findings.Count > 0)
            {
                sb.AppendLine("\nPosted Findings:");
                foreach (var f in snapshot.Result.Findings)
                    sb.AppendLine($"- [{f.Severity}] `{f.FilePath}:{f.StartLine}`: {f.Message}");
            }

            if (snapshot.Files.Count > 0)
            {
                sb.AppendLine("\nReviewed Files:");
                foreach (var f in snapshot.Files)
                    sb.AppendLine($"- {f.Path} ({f.Kind})");
            }

            sb.AppendLine("</review_snapshot>");
        }

        if (threadContext.FilePath is not null)
        {
            sb.AppendLine("\n<thread_anchor>");
            sb.AppendLine($"File: {threadContext.FilePath}");
            if (threadContext.StartLine is not null)
                sb.AppendLine($"Line: {threadContext.StartLine}");

            if (threadContext.Fingerprint is not null && snapshot is not null)
            {
                var matchedFinding = snapshot.Result.Findings
                    .FirstOrDefault(f => Finding.Fingerprint(f) == threadContext.Fingerprint);
                if (matchedFinding is not null)
                    sb.AppendLine($"Finding: [{matchedFinding.Severity}] {matchedFinding.Message}");
            }

            sb.AppendLine("</thread_anchor>");
        }

        var humanMessages = threadContext.ThreadMessages
            .Where(m => !m.StartsWith(ChatMarker))
            .ToList();

        if (humanMessages.Count > 0)
        {
            sb.AppendLine("\n<thread_conversation>");
            foreach (var msg in humanMessages)
                sb.AppendLine(msg);
            sb.AppendLine("</thread_conversation>");
        }

        return sb.ToString();
    }

    private static string BuildSummary(string modelSummary, List<Finding> summaryFindings)
    {
        if (summaryFindings.Count == 0)
            return modelSummary;

        var sb = new StringBuilder(modelSummary);
        sb.Append($"\n\n<details>\n<summary>Additional findings ({summaryFindings.Count})</summary>\n\n");
        foreach (var f in summaryFindings)
            sb.AppendLine($"- `{f.FilePath}:{f.StartLine}` — {f.Message}");
        sb.Append("</details>");
        return sb.ToString();
    }
}
