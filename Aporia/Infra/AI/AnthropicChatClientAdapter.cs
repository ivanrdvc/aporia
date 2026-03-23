using System.Text.Json;

using Anthropic.Models.Messages;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Aporia.Infra.AI;

/// <summary>
/// Bridges MEAI options the Anthropic SDK silently ignores:
/// <see cref="ChatOptions.ResponseFormat"/> → <see cref="OutputConfig"/> and
/// <see cref="ChatOptions.Reasoning"/> → <see cref="ThinkingConfigParam"/>.
/// Both are applied via <see cref="ChatOptions.RawRepresentationFactory"/>.
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
        if (options is null) return options;

        var hasSchema = options.ResponseFormat is ChatResponseFormatJson { Schema: not null };
        var hasReasoning = options.Reasoning is not null;

        if (!hasSchema && !hasReasoning) return options;

        _model ??= InnerClient.GetService<ChatClientMetadata>()?.DefaultModelId
                    ?? throw new InvalidOperationException("Anthropic IChatClient did not expose model metadata.");

        OutputConfig? outputConfig = null;
        if (hasSchema)
        {
            var jsonFormat = (ChatResponseFormatJson)options.ResponseFormat!;
            var strict = AddAdditionalPropertiesFalse(jsonFormat.Schema!.Value);
            var schemaDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(strict.GetRawText())
                             ?? throw new InvalidOperationException("Failed to deserialize JSON schema from ChatResponseFormatJson.");
            outputConfig = new OutputConfig { Format = new JsonOutputFormat { Schema = schemaDict } };
        }

        // The Anthropic SDK ignores ChatOptions.Reasoning — translate to native ThinkingConfigParam.
        ThinkingConfigParam? thinking = null;
        if (hasReasoning)
        {
            var budget = options.Reasoning!.Effort switch
            {
                ReasoningEffort.Low => 2048,
                ReasoningEffort.Medium => 4096,
                ReasoningEffort.High => 8192,
                _ => 4096
            };
            thinking = new ThinkingConfigParam(new ThinkingConfigEnabled(budgetTokens: budget));
        }

        var adapted = options.Clone();
        adapted.RawRepresentationFactory = _ => new MessageCreateParams
        {
            Model = _model,
            MaxTokens = options.MaxOutputTokens ?? DefaultMaxOutputTokens,
            Messages = [],
            OutputConfig = outputConfig,
            Thinking = thinking,
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
