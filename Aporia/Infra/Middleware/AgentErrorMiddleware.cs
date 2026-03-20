using System.Diagnostics;
using System.Net.Sockets;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Aporia.Infra.Middleware;

public static class AgentErrorMiddleware
{
    public static async Task<AgentResponse> Handle(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken,
        ILogger logger)
    {
        var sw = Stopwatch.StartNew();

        var chatMessages = messages as ChatMessage[] ?? messages.ToArray();
        try
        {
            return await innerAgent.RunAsync(chatMessages, session, options, cancellationToken);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException or SocketException or IOException)
        {
            logger.LogError(ex,
                "Agent {Agent} timed out after {Elapsed:F1}s ({MessageCount} messages)",
                innerAgent.Name, sw.Elapsed.TotalSeconds, chatMessages.Length);
            throw;
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex,
                "Agent {Agent} failed after {Elapsed:F1}s ({MessageCount} messages): HTTP {StatusCode}",
                innerAgent.Name, sw.Elapsed.TotalSeconds, chatMessages.Length, ex.StatusCode);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Agent {Agent} failed after {Elapsed:F1}s ({MessageCount} messages)",
                innerAgent.Name, sw.Elapsed.TotalSeconds, chatMessages.Length);
            throw;
        }
    }
}
