using System.Text.Json;

using Anthropic.Models.Messages;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Aporia.Infra.AI;

/// <summary>
/// Translates MEAI <see cref="ChatOptions.ResponseFormat"/> to Anthropic-native
/// <see cref="OutputConfig"/> via <see cref="ChatOptions.RawRepresentationFactory"/>.
/// The official Anthropic SDK's IChatClient adapter silently ignores ResponseFormat,
/// so this middleware bridges the gap.
/// </summary>
public sealed class AnthropicChatClientAdapter(IChatClient inner, ILogger<AnthropicChatClientAdapter> logger)
    : DelegatingChatClient(inner)
{
    private const int DefaultMaxOutputTokens = 16_384;

    private string? _model;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var adapted = Adapt(options);
        var response = await base.GetResponseAsync(messages, adapted, cancellationToken);

        // Log diagnostics when a structured-output call returns empty text.
        // Captures the Anthropic stop_reason and raw content count so we can
        // identify whether the API returned a refusal, max_tokens, or empty content.
        if (adapted != options)
        {
            var lastMsg = response.Messages.LastOrDefault();
            if (lastMsg is not null
                && !ChatFinishReason.ToolCalls.Equals(response.FinishReason)
                && string.IsNullOrEmpty(lastMsg.Text))
            {
                var rawMessage = response.RawRepresentation as Message;
                logger.LogWarning(
                    "Structured output returned empty text. " +
                    "FinishReason={FinishReason}, StopReason={StopReason}, ContentCount={ContentCount}, ContentTypes={ContentTypes}",
                    response.FinishReason,
                    rawMessage?.StopReason?.Raw(),
                    rawMessage?.Content?.Count,
                    rawMessage?.Content is { } content
                        ? string.Join(", ", content.Select(c => c.Json.TryGetProperty("type", out var t) ? t.GetString() : "unknown"))
                        : "null");
            }
        }

        return response;
    }

    private ChatOptions? Adapt(ChatOptions? options)
    {
        if (options?.ResponseFormat is not ChatResponseFormatJson jsonFormat || jsonFormat.Schema is null)
            return options;

        _model ??= InnerClient.GetService<ChatClientMetadata>()?.DefaultModelId
                    ?? throw new InvalidOperationException("Anthropic IChatClient did not expose model metadata.");

        var strict = AddAdditionalPropertiesFalse(jsonFormat.Schema.Value);
        var schemaDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(strict.GetRawText())
                         ?? throw new InvalidOperationException("Failed to deserialize JSON schema from ChatResponseFormatJson.");

        var outputConfig = new OutputConfig
        {
            Format = new JsonOutputFormat { Schema = schemaDict }
        };

        var adapted = options.Clone();
        adapted.RawRepresentationFactory = _ => new MessageCreateParams
        {
            Model = _model,
            MaxTokens = options.MaxOutputTokens ?? DefaultMaxOutputTokens,
            Messages = [],
            OutputConfig = outputConfig
        };
        return adapted;
    }

    /// <summary>
    /// Recursively adds "additionalProperties": false to every object-type node in the schema.
    /// Claude's structured output requires this on all object types.
    /// </summary>
    private static JsonElement AddAdditionalPropertiesFalse(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => PatchObject(element),
        JsonValueKind.Array => JsonSerializer.SerializeToElement(element.EnumerateArray().Select(AddAdditionalPropertiesFalse)),
        _ => element
    };

    private static JsonElement PatchObject(JsonElement element)
    {
        var dict = new Dictionary<string, JsonElement>();
        var isObject = false;

        foreach (var prop in element.EnumerateObject())
        {
            if (prop is { Name: "type", Value.ValueKind: JsonValueKind.String } && prop.Value.GetString() == "object")
                isObject = true;

            dict[prop.Name] = AddAdditionalPropertiesFalse(prop.Value);
        }

        if (isObject && !dict.ContainsKey("additionalProperties"))
            dict["additionalProperties"] = JsonSerializer.SerializeToElement(false);

        return JsonSerializer.SerializeToElement(dict);
    }
}
