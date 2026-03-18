using Microsoft.Extensions.AI;

namespace Aporia.Tests.Eval.TestHelpers;

/// <summary>
/// A <see cref="DelegatingChatClient"/> that records all messages exchanged with the inner client.
/// Captures input messages, response messages (including <see cref="FunctionCallContent"/>),
/// and the final <see cref="ChatResponse"/>.
/// </summary>
public sealed class CapturingChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    private readonly List<ChatMessage> _messages = [];
    private ChatResponse? _lastResponse;
    private readonly Lock _lock = new();
    private int _lastInputCount;

    /// <summary>All messages exchanged (inputs + response messages), in order.</summary>
    public IReadOnlyList<ChatMessage> Messages
    {
        get { lock (_lock) return [.. _messages]; }
    }

    /// <summary>The last <see cref="ChatResponse"/> returned by the inner client.</summary>
    public ChatResponse? LastResponse
    {
        get { lock (_lock) return _lastResponse; }
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var inputMessages = messages.ToList();
        var response = await base.GetResponseAsync(inputMessages, options, cancellationToken);

        lock (_lock)
        {
            // Only capture messages added since the last call to avoid duplicating
            // the growing conversation history across multi-turn orchestrator loops.
            foreach (var msg in inputMessages.Skip(_lastInputCount))
                _messages.Add(msg);
            _lastInputCount = inputMessages.Count;

            _messages.AddRange(response.Messages);
            _lastResponse = response;
        }

        return response;
    }
}
