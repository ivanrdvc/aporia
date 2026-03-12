using System.Text;

using Microsoft.Agents.AI;

using Revu.Infra;

namespace Revu.Review;

public sealed class PrContextProvider : AIContextProvider
{
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();

        // PR metadata
        if (context.Session?.StateBag.TryGetValue<PrContext>(SessionKeys.PrContext, out var pr) == true
            && pr is not null)
        {
            sb.AppendLine("<pr_context>");
            sb.AppendLine($"PR Title: {pr.Title}");

            if (!string.IsNullOrWhiteSpace(pr.Description))
                sb.AppendLine($"PR Description: {pr.Description}");

            if (pr.CommitMessages.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Commit messages:");
                foreach (var msg in pr.CommitMessages)
                    sb.AppendLine($"- {msg}");
            }

            sb.AppendLine("</pr_context>");
        }

        // Project context and rules from .revu.json
        if (context.Session?.StateBag.TryGetValue<ProjectConfig>(SessionKeys.ProjectConfig, out var config) == true
            && config is not null)
        {
            if (config.Context is not null)
                sb.AppendLine($"\n<project_context>\n{config.Context}\n</project_context>");

            if (config.Rules.Count > 0)
            {
                sb.AppendLine("\n<additional_rules>");
                foreach (var rule in config.Rules)
                    sb.AppendLine($"- {rule}");
                sb.AppendLine("</additional_rules>");
            }
        }

        return sb.Length > 0
            ? new(new AIContext { Instructions = sb.ToString() })
            : new(new AIContext());
    }
}
