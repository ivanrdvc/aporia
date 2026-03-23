using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Aporia.Infra;
using Aporia.Infra.Telemetry;

namespace Aporia.Review;

internal static class AgentResponseExtensions
{
    /// <summary>
    /// Extracts <typeparamref name="T"/> from the agent's response by scanning
    /// all assistant messages for the best parseable result. Anthropic models can
    /// emit structured output alongside tool calls; when MEAI's function invocation
    /// loop continues, a later (often empty) response overwrites the valid one.
    /// Scanning all messages preserves the best result regardless of ordering.
    /// </summary>
    internal static T? ExtractResult<T>(this AgentResponse response, ILogger logger) where T : class
    {
        if (response.FinishReason is { } reason && reason != ChatFinishReason.Stop)
        {
            logger.LogWarning("Agent finished with reason {FinishReason}", reason);
            Telemetry.AgentAbnormalFinish.Add(1);
        }

        // Anthropic returns text + tool calls in one response (OpenAI never does this).
        // MEAI's loop then calls the LLM again, which often revises findings downward.
        // We scan all messages and keep the longest valid parse to survive self-revision.
        T? best = null;
        var bestLength = 0;

        foreach (var msg in response.Messages)
        {
            if (msg.Role != ChatRole.Assistant) continue;
            var text = msg.Text;
            if (string.IsNullOrEmpty(text)) continue;

            var parsed = text.TryParseJson<T>();
            if (parsed is not null && text.Length > bestLength)
            {
                best = parsed;
                bestLength = text.Length;
            }
        }

        if (best is not null)
        {
            var lastAssistantText = response.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant)?.Text;
            if (lastAssistantText is not null && lastAssistantText.Length < bestLength)
            {
                logger.LogInformation("Used earlier assistant message ({Length} chars) over later revision ({LastLength} chars)",
                    bestLength, lastAssistantText.Length);
            }
            return best;
        }

        // No parseable result found — check if the model returned anything at all.
        var lastText = response.Messages.LastOrDefault()?.Text ?? "";
        if (lastText.Length == 0)
        {
            logger.LogInformation("Model returned empty response");
            return null;
        }

        logger.LogWarning("Failed to parse {Type} from {MsgCount} messages (last message: {Length} chars)",
            typeof(T).Name, response.Messages.Count, lastText.Length);
        Telemetry.ParseFailures.Add(1);
        return null;
    }
}
