using System.Text;

using GitHub.Copilot.SDK;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Aporia.Git;
using Aporia.Infra.AI;
using Aporia.Infra.Telemetry;

namespace Aporia.Review;

public class CopilotStrategy(
    [FromKeyedServices(ModelKey.Default)] IChatClient extractionClient,
    IOptions<AdoOptions> adoOptions,
    IOptions<GitHubOptions> ghOptions,
    GitHubTokenService gitHubTokenService,
    ILogger<CopilotStrategy> logger) : IReviewStrategy
{
    // Copilot operates on a local clone; code graph is not applicable.
    public async Task<ReviewResult> Review(
        ReviewRequest req, Diff diff, ProjectConfig config, PrContext prContext,
        CodeGraph.CodeGraphQuery? codeGraph = null, CancellationToken ct = default)
    {
        var (cloneUrl, token) = await GetCloneCredentials(req);

        await using var clone = await RepoClone.CreateAsync(cloneUrl, req.SourceBranch, token, ct);

        logger.LogInformation("Cloned {Repo} ({Branch}) to {Path}", req.RepositoryName, req.SourceBranch, clone.Path);

        await using var client = new CopilotClient(new CopilotClientOptions { AutoStart = false });
        await client.StartAsync(ct);

        var agent = client.AsAIAgent(
            sessionConfig: new SessionConfig
            {
                WorkingDirectory = clone.Path,
                SystemMessage = new SystemMessageConfig
                {
                    Mode = SystemMessageMode.Append,
                    Content = Prompts.ReviewerInstructions
                },
                OnPermissionRequest = HandlePermission,
            });

        var prompt = Prompts.BuildReviewPrompt(diff);
        logger.LogInformation("Sending prompt ({Length} chars) to Copilot agent...", prompt.Length);

        var session = await agent.CreateSessionAsync(cancellationToken: ct);

        // Use streaming — RunAsync aggregation loses text because the MAF adapter wraps
        // AssistantMessageEvent as base AIContent instead of TextContent.
        var sb = new StringBuilder();
        await foreach (var update in agent.RunStreamingAsync(prompt, session, cancellationToken: ct))
        {
            foreach (var content in update.Contents.OfType<TextContent>())
                sb.Append(content.Text);
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
            {responseText}
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

    private async Task<(string cloneUrl, string token)> GetCloneCredentials(ReviewRequest req) => req.Provider switch
    {
        GitProvider.GitHub => (
            $"https://github.com/{req.Organization}/{req.RepositoryName}.git",
            await GetGitHubToken(req)),

        GitProvider.Ado => (
            $"https://dev.azure.com/{req.Organization}/{req.Project}/_git/{req.RepositoryName}",
            adoOptions.Value.Organizations.Values
                .FirstOrDefault(o => o.Organization == req.Organization)
                ?.PersonalAccessToken
                ?? throw new InvalidOperationException(
                    $"No ADO configuration found for organization '{req.Organization}'.")),

        _ => throw new NotSupportedException($"Provider {req.Provider} is not supported by CopilotStrategy.")
    };

    private Task<string> GetGitHubToken(ReviewRequest req)
    {
        var config = ghOptions.Value;

        if (config.UseApp && req.InstallationId is { } installationId)
            return gitHubTokenService.GetInstallationTokenAsync(config, installationId);

        if (!string.IsNullOrWhiteSpace(config.Token))
            return Task.FromResult(config.Token);

        throw new InvalidOperationException(
            "No GitHub auth available for cloning. Configure a PAT or GitHub App credentials.");
    }
}
