using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Aporia.Infra;

/// <summary>
/// Write-only session provider that captures each agent invocation to a numbered JSON file.
/// Returns no history (fresh session every run).
/// </summary>
public sealed class FileSessionProvider(string directory) : ChatHistoryProvider
{
    private int _counter;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context, CancellationToken cancellationToken = default) => new([]);

    protected override async ValueTask StoreChatHistoryAsync(
        InvokedContext context, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        var idx = Interlocked.Increment(ref _counter);

        var agentName = context.Agent is ChatClientAgent chatAgent ? chatAgent.Name : null;
        var instructions = context.Agent is ChatClientAgent ca ? ca.Instructions : null;
        var chatOptions = context.Agent.GetService<ChatOptions>();

        var tools = chatOptions?.Tools?
            .OfType<AIFunctionDeclaration>()
            .Select(t => new ToolDef(t.Name, t.Description, t.JsonSchema))
            .ToList();

        var messages = context.ResponseMessages is not null
            ? context.RequestMessages.Concat(context.ResponseMessages).ToList()
            : context.RequestMessages.ToList();

        var envelope = new SessionEnvelope
        {
            Agent = agentName,
            Instructions = instructions,
            Tools = tools?.Count > 0 ? tools : null,
            Messages = messages,
            Error = context.InvokeException?.Message
        };

        await File.WriteAllTextAsync(
            Path.Combine(directory, $"{idx:D2}.json"),
            JsonSerializer.Serialize(envelope, JsonOptions), ct);
    }

    private sealed class SessionEnvelope
    {
        [JsonPropertyName("agent")]
        public string? Agent { get; init; }

        [JsonPropertyName("instructions")]
        public string? Instructions { get; init; }

        [JsonPropertyName("tools")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ToolDef>? Tools { get; init; }

        [JsonPropertyName("messages")]
        public required List<ChatMessage> Messages { get; init; }

        [JsonPropertyName("error")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Error { get; init; }
    }

    private sealed record ToolDef(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("schema")] JsonElement Schema);
}
