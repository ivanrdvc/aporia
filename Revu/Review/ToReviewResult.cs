using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

using Revu.Infra;
using Revu.Infra.Telemetry;

namespace Revu.Review;

internal static class AgentResponseExtensions
{
    /// <summary>
    /// Extracts <see cref="ReviewResult"/> from the agent's last message.
    /// Uses LastOrDefault().Text instead of response.Text which concatenates
    /// all intermediate messages and corrupts structured JSON (agent-framework#2796).
    /// </summary>
    internal static ReviewResult ExtractReviewResult(this AgentResponse response, ILogger logger)
    {
        var text = response.Messages.LastOrDefault()?.Text ?? "";
        var result = text.TryParseJson<ReviewResult>();

        if (result is null && text.Length == 0)
        {
            // Anthropic API returns empty content array when the model has nothing to say
            // (thinking tokens consumed but no text produced). This is expected behavior,
            // not a parse failure. Treat as zero findings.
            logger.LogInformation("Model returned empty response (zero findings)");
            return new([], "No issues found.");
        }

        if (result is null)
        {
            logger.LogWarning("Failed to parse ReviewResult from last message ({Length} chars, {MsgCount} messages)",
                text.Length, response.Messages.Count);
            Telemetry.ParseFailures.Add(1);
        }
        else if (result.Findings.Count == 0)
            logger.LogWarning("Model returned zero findings");

        return result ?? new([], "Review completed but failed to parse structured output.");
    }
}
