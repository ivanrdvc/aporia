using System.ClientModel;

using Anthropic;

using Azure.AI.OpenAI;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using OpenAI;

namespace Aporia.Infra.AI;

public static class ChatClientExtensions
{
    public static void AddChatClients(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AIOptions>().BindConfiguration(AIOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();

        var options = configuration.GetSection(AIOptions.SectionName).Get<AIOptions>()
                      ?? throw new InvalidOperationException("AI configuration section is missing.");

        foreach (var (name, value) in options.Models)
        {
            var (provider, model) = ParseModel(name, value);

            var builder = services.AddKeyedChatClient(name, _ => CreateClient(provider, model, options));

            if (provider == "anthropic")
                builder.Use((inner, sp) => new AnthropicChatClientAdapter(inner, sp.GetRequiredService<ILogger<AnthropicChatClientAdapter>>()));

            builder
                .UseLogging()
                .UseOpenTelemetry(sourceName: Telemetry.Telemetry.ServiceName, configure: c => c.EnableSensitiveData = true);
        }
    }

    private static (string Provider, string Model) ParseModel(string key, string value)
    {
        var slash = value.IndexOf('/');
        if (slash <= 0 || slash == value.Length - 1)
            throw new InvalidOperationException(
                $"AI:Models:{key} value '{value}' is not in 'provider/model' format (e.g. 'openai/gpt-4o-mini').");

        return (value[..slash].ToLowerInvariant(), value[(slash + 1)..]);
    }

    private static IChatClient CreateClient(string provider, string model, AIOptions options) => provider switch
    {
        "openai" when options.OpenAi is { } openAi =>
            new OpenAIClient(openAi.ApiKey)
                .GetChatClient(model)
                .AsIChatClient(),

        "azure" when options.AzureOpenAi is { } azure =>
            new AzureOpenAIClient(new Uri(azure.Endpoint), new ApiKeyCredential(azure.ApiKey))
                .GetChatClient(model)
                .AsIChatClient(),

        "anthropic" when options.Anthropic is { } anthropic =>
            new AnthropicClient
            {
                ApiKey = anthropic.ApiKey,
                HttpClient = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan },
            }
                .AsIChatClient(model, defaultMaxOutputTokens: 16_384),

        "openai" or "azure" or "anthropic" =>
            throw new InvalidOperationException(
                $"AI:{provider} configuration is required when using the '{provider}' provider."),

        _ => throw new InvalidOperationException(
            $"Unknown AI provider: '{provider}'. Use 'openai', 'azure', or 'anthropic'."),
    };
}
