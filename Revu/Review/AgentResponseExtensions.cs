using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Revu.Infra;
using Revu.Infra.Telemetry;

namespace Revu.Review;

internal static class AgentResponseExtensions
{
    /// <summary>
    /// Extracts <typeparamref name="T"/> from the agent's last message.
    /// Uses LastOrDefault().Text instead of response.Text which concatenates
    /// all intermediate messages and corrupts structured JSON (agent-framework#2796).
    /// Returns null when the response is empty or fails to parse.
    /// </summary>
    internal static T? ExtractResult<T>(this AgentResponse response, ILogger logger) where T : class
    {
        if (response.FinishReason is { } reason && reason != ChatFinishReason.Stop)
        {
            logger.LogWarning("Agent finished with reason {FinishReason}", reason);
            Telemetry.AgentMaxTurnsHit.Add(1);
        }

        var text = response.Messages.LastOrDefault()?.Text ?? "";
        var result = text.TryParseJson<T>();

        if (result is null && text.Length == 0)
        {
            // Anthropic API returns empty content array when the model has nothing to say
            // (thinking tokens consumed but no text produced). This is expected behavior.
            logger.LogInformation("Model returned empty response");
            return null;
        }

        if (result is null)
        {
            logger.LogWarning("Failed to parse {Type} from last message ({Length} chars, {MsgCount} messages)",
                typeof(T).Name, text.Length, response.Messages.Count);
            Telemetry.ParseFailures.Add(1);
        }

        return result;
    }
}
