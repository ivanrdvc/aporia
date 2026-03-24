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
///
/// Also handles a Haiku behavioral issue: when tools, thinking, and structured
/// output are all present, the model can produce a thinking-only response without
/// emitting text. When detected, retries the call with tools stripped to force
/// structured output generation.
/// </summary>
public sealed class AnthropicChatClientAdapter(IChatClient inner, ILogger<AnthropicChatClientAdapter> logger)
    : DelegatingChatClient(inner)
{
    private const int DefaultMaxOutputTokens = 64_000;

    private string? _model;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var adapted = Adapt(options);
        var response = await base.GetResponseAsync(messages, adapted, cancellationToken);

        if (adapted == options || ChatFinishReason.ToolCalls.Equals(response.FinishReason))
            return response;

        var lastMsg = response.Messages.LastOrDefault();
        if (lastMsg is null || !string.IsNullOrEmpty(lastMsg.Text))
            return response;

        // The model returned no text. When tools are present this can happen because
        // the model gets stuck between calling tools and producing schema-constrained
        // output. Retry once without tools to force structured output generation.
        if (adapted?.Tools is { Count: > 0 })
        {
            logger.LogWarning("Thinking-only response with tools present — retrying without tools");
            var retryOptions = adapted.Clone();
            retryOptions.Tools = null;
            retryOptions.ToolMode = null;
            return await base.GetResponseAsync(messages, retryOptions, cancellationToken);
        }

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
    /// Recursively patches the schema for Anthropic compatibility:
    /// 1. Resolves all <c>$ref</c> pointers by inlining definitions from <c>$defs</c>.
    /// 2. Adds <c>"additionalProperties": false</c> to every object-type node.
    /// </summary>
    private static JsonElement AddAdditionalPropertiesFalse(JsonElement element) =>
        AddAdditionalPropertiesFalse(element, ResolveDefsMap(element));

    private static JsonElement AddAdditionalPropertiesFalse(JsonElement element, Dictionary<string, JsonElement> defs) => element.ValueKind switch
    {
        JsonValueKind.Object => PatchObject(element, defs),
        JsonValueKind.Array => JsonSerializer.SerializeToElement(element.EnumerateArray().Select(e => AddAdditionalPropertiesFalse(e, defs))),
        _ => element
    };

    private static JsonElement PatchObject(JsonElement element, Dictionary<string, JsonElement> defs)
    {
        // Resolve $ref by inlining the referenced definition
        if (element.TryGetProperty("$ref", out var refValue))
        {
            var refPath = refValue.GetString();
            if (refPath is not null && defs.TryGetValue(refPath, out var resolved))
                return AddAdditionalPropertiesFalse(resolved, defs);
        }

        var dict = new Dictionary<string, JsonElement>();
        var isObject = false;

        foreach (var prop in element.EnumerateObject())
        {
            // Drop $defs from the output — all refs are now inlined
            if (prop.Name is "$defs" or "definitions")
                continue;

            if (prop is { Name: "type", Value.ValueKind: JsonValueKind.String } && prop.Value.GetString() == "object")
                isObject = true;

            dict[prop.Name] = AddAdditionalPropertiesFalse(prop.Value, defs);
        }

        if (isObject && !dict.ContainsKey("additionalProperties"))
            dict["additionalProperties"] = JsonSerializer.SerializeToElement(false);

        return JsonSerializer.SerializeToElement(dict);
    }

    /// <summary>
    /// Builds a map of all JSON pointer paths to their JsonElement values so that
    /// <c>$ref</c> pointers (both <c>#/$defs/...</c> and <c>#/properties/...</c>) can be resolved.
    /// </summary>
    private static Dictionary<string, JsonElement> ResolveDefsMap(JsonElement root)
    {
        var defs = new Dictionary<string, JsonElement>();
        CollectPaths(root, "#", defs);
        return defs;
    }

    private static void CollectPaths(JsonElement element, string path, Dictionary<string, JsonElement> paths)
    {
        paths[path] = element;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
                CollectPaths(prop.Value, $"{path}/{prop.Name}", paths);
        }
    }
}
