using System.ComponentModel.DataAnnotations;

namespace Aporia.Infra.AI;

public class AIOptions
{
    public const string SectionName = "Ai";
    private const int DefaultContextWindow = 1_000_000;

    /// Named models in "provider/model" format (e.g., "default": "openai/gpt-4o-mini", "reasoning": "openai/o3-mini")
    [Required, MinLength(1)]
    public required Dictionary<string, string> Models { get; init; }

    /// Token context window per model key (e.g., "reasoning": 200000). Defaults to 1M.
    public Dictionary<string, int> ContextWindows { get; init; } = [];

    public OpenAIProviderOptions? OpenAi { get; init; }
    public AzureOpenAIProviderOptions? AzureOpenAi { get; init; }
    public AnthropicProviderOptions? Anthropic { get; init; }

    public int GetContextWindow(string modelKey) => ContextWindows.GetValueOrDefault(modelKey, DefaultContextWindow);
}

public class OpenAIProviderOptions
{
    [Required]
    public required string ApiKey { get; init; }
}

public class AzureOpenAIProviderOptions
{
    [Required]
    public required string Endpoint { get; init; }

    [Required]
    public required string ApiKey { get; init; }
}

public class AnthropicProviderOptions
{
    [Required]
    public required string ApiKey { get; init; }
}

public static class ModelKey
{
    public const string Default = "default";
    public const string Reasoning = "reasoning";
}
