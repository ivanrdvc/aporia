using System.Text;

using GitHub.Copilot.SDK;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Aporia.Git;
using Aporia.Infra.AI;
using Aporia.Infra.Telemetry;

namespace Aporia.Review;

public class CopilotStrategy(
    [FromKeyedServices(ModelKey.Default)] IChatClient extractionClient,
    IServiceProvider sp,
    ILogger<CopilotStrategy> logger) : IReviewStrategy
{
    private static readonly TimeSpan AgentTimeout = TimeSpan.FromMinutes(5);

    public async Task<ReviewResult> Review(
        ReviewRequest req, Diff diff, ProjectConfig config, PrContext prContext,
        CodeGraph.CodeGraphQuery? codeGraph = null, CancellationToken ct = default)
    {
        // Shared budget for clone + agent + extraction. Distinguishes our timeout from caller cancellation.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(AgentTimeout);

        var git = sp.GetRequiredKeyedService<IGitConnector>(req.Provider);
        var creds = await git.GetCloneCredentials(req);

        await using var clone = await RepoClone.CreateAsync(creds.Url, req.SourceBranch, creds.Token, cts.Token);

        logger.LogInformation("Cloned {Repo} ({Branch}) to {Path}", req.RepositoryName, req.SourceBranch, clone.Path);

        await using var client = new CopilotClient(new CopilotClientOptions { AutoStart = false });
        await client.StartAsync(cts.Token);

        // SessionConfig doesn't support AIContextProviders, so manually build the
        // same context that PrContextProvider would inject for CoreStrategy.
        var contextInstructions = PrContextProvider.BuildInstructions(prContext, config);
        var systemMessage = string.IsNullOrEmpty(contextInstructions)
            ? Prompts.ReviewerInstructions
            : $"{Prompts.ReviewerInstructions}\n\n{contextInstructions}";

        var agent = client.AsAIAgent(
            sessionConfig: new SessionConfig
            {
                WorkingDirectory = clone.Path,
                SystemMessage = new SystemMessageConfig
                {
                    Mode = SystemMessageMode.Append,
                    Content = systemMessage
                },
                OnPermissionRequest = HandlePermission,
            });

        var prompt = Prompts.BuildReviewPrompt(diff);
        logger.LogInformation("Sending prompt ({Length} chars) to Copilot agent...", prompt.Length);

        var session = await agent.CreateSessionAsync(cancellationToken: cts.Token);

        // Use streaming — RunAsync aggregation loses text because the MAF adapter wraps
        // AssistantMessageEvent as base AIContent instead of TextContent.
        var sb = new StringBuilder();
        try
        {
            await foreach (var update in agent.RunStreamingAsync(prompt, session, cancellationToken: cts.Token))
            {
                foreach (var content in update.Contents.OfType<TextContent>())
                    sb.Append(content.Text);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our timeout fired, not the caller's cancellation.
            // Partial response may still be usable for extraction.
            logger.LogWarning("Copilot agent timed out after {Timeout}", AgentTimeout);
        }

        var responseText = sb.ToString();
        logger.LogInformation("Copilot response: {Length} chars", responseText.Length);

        if (string.IsNullOrWhiteSpace(responseText))
            return new ReviewResult([], "Copilot returned an empty response.");

        // Copilot has no structured output — extract ReviewResult via a cheap IChatClient pass.
        logger.LogInformation("Extracting structured ReviewResult from Copilot response...");

        var extraction = await extractionClient.GetResponseAsync<ReviewResult>(
            $"""
            Extract the code review findings from the following review into the requested JSON schema.
            Map each finding to the correct file path, line numbers, severity, and message.
            If the review mentions no issues, return an empty findings list.
            Preserve the summary as-is.

            ---
            <copilot_response>
            {responseText}
            </copilot_response>
            """,
            new ChatOptions
            {
                ResponseFormat = ChatResponseFormat.ForJsonSchema<ReviewResult>(),
            },
            cancellationToken: ct);

        var result = extraction.Result;

        if (result is null)
        {
            logger.LogWarning("Structured extraction returned null");
            Telemetry.CopilotExtractionFailures.Add(1);
            return new ReviewResult([], "Copilot review completed but structured extraction failed.");
        }

        logger.LogInformation("Extracted {Count} findings from Copilot response", result.Findings.Count);
        return result;
    }

    private Task<PermissionRequestResult> HandlePermission(PermissionRequest permReq, PermissionInvocation _)
    {
        var allowed = permReq.Kind is "read" or "search" or "list" or "file_read" or "search_code" or "list_directory";

        var ext = permReq.ExtensionData is { Count: > 0 }
            ? string.Join(", ", permReq.ExtensionData.Select(kv => $"{kv.Key}={kv.Value}"))
            : "none";

        logger.LogInformation("Permission {Decision}: Kind={Kind}, ExtensionData=[{Ext}]",
            allowed ? "approved" : "denied", permReq.Kind, ext);

        return Task.FromResult(new PermissionRequestResult { Kind = allowed ? "approved" : "denied" });
    }

}
