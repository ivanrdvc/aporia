using System.Net.Http.Headers;

using Microsoft.Extensions.DependencyInjection;
using Aporia.DocWatch;

namespace Aporia.Git;

public static class GitServiceExtensions
{
    private const string DocWatchHttpClient = "github-docwatch";

    public static IServiceCollection AddGitHub(this IServiceCollection services)
    {
        services.AddOptions<GitHubOptions>().BindConfiguration(GitHubOptions.SectionName).ValidateDataAnnotations();
        services.AddSingleton<GitHubTokenService>();
        services.AddTransient<GitHubAuthHandler>();
        services.AddHttpClient<GitHubConnector>(ConfigureGitHubClient).AddHttpMessageHandler<GitHubAuthHandler>();
        services.AddHttpClient(GitHubTokenService.TokenClientName, ConfigureGitHubClient);
        services.AddHttpClient(DocWatchHttpClient, ConfigureGitHubClient).AddHttpMessageHandler<GitHubAuthHandler>();
        services.AddKeyedScoped<IGitConnector>(GitProvider.GitHub, (sp, _) => sp.GetRequiredService<GitHubConnector>());
        services.AddKeyedSingleton<IDocPublisher>(GitProvider.GitHub, (sp, _) =>
            new GitHubDocPublisher(sp.GetRequiredService<IHttpClientFactory>().CreateClient(DocWatchHttpClient)));

        return services;
    }

    public static IServiceCollection AddAdo(this IServiceCollection services)
    {
        services.AddOptions<AdoOptions>().BindConfiguration(AdoOptions.SectionName).ValidateDataAnnotations();
        services.AddHttpClient(AdoConnector.SearchClientName);
        services.AddKeyedSingleton<IGitConnector, AdoConnector>(GitProvider.Ado);
        services.AddKeyedSingleton<IDocPublisher, AdoDocPublisher>(GitProvider.Ado);

        return services;
    }

    private static void ConfigureGitHubClient(HttpClient client)
    {
        client.BaseAddress = new Uri("https://api.github.com/");
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Aporia", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }
}
