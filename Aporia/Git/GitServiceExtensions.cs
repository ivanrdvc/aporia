using System.Net.Http.Headers;

using Microsoft.Extensions.DependencyInjection;

namespace Aporia.Git;

public static class GitServiceExtensions
{
    public static IServiceCollection AddGitHub(this IServiceCollection services)
    {
        services.AddOptions<GitHubOptions>().BindConfiguration(GitHubOptions.SectionName).ValidateDataAnnotations();
        services.AddTransient<GitHubAuthHandler>();
        services.AddHttpClient<GitHubConnector>(ConfigureGitHubClient).AddHttpMessageHandler<GitHubAuthHandler>();
        services.AddHttpClient(GitHubAuthHandler.TokenClientName, ConfigureGitHubClient);
        services.AddKeyedScoped<IGitConnector>(GitProvider.GitHub, (sp, _) => sp.GetRequiredService<GitHubConnector>());

        return services;
    }

    public static IServiceCollection AddAdo(this IServiceCollection services)
    {
        services.AddOptions<AdoOptions>().BindConfiguration(AdoOptions.SectionName).ValidateDataAnnotations();
        services.AddHttpClient(AdoConnector.SearchClientName);
        services.AddKeyedSingleton<IGitConnector, AdoConnector>(GitProvider.Ado);

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
