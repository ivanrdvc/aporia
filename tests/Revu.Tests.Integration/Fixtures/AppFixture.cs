using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.TeamFoundation.SourceControl.WebApi;

using Revu.Git;
using Revu.Infra;
using Revu.Infra.AI;
using Revu.Infra.Cosmos;
using Revu.Infra.Telemetry;
using Revu.Review;

namespace Revu.Tests.Integration.Fixtures;

public class AppFixture : IAsyncLifetime
{
    private readonly IHost _host;
    public IServiceProvider Services => _host.Services;
    public string SessionDirectory { get; }

    public AppFixture()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });

        builder.Configuration
            .AddJsonFile("appsettings.test.json", optional: true)
            .AddUserSecrets<AppFixture>()
            .AddEnvironmentVariables();

        // Options
        builder.Services.Configure<AIOptions>(builder.Configuration.GetSection(AIOptions.SectionName));
        builder.Services.Configure<CosmosOptions>(builder.Configuration.GetSection(CosmosOptions.SectionName));
        builder.Services.Configure<RevuOptions>(builder.Configuration.GetSection(RevuOptions.SectionName));

        // Resolve test profile: TEST_PROFILE env var → TestProfile in JSON
        var profileName = Environment.GetEnvironmentVariable("TEST_PROFILE")
                          ?? builder.Configuration.GetValue<string>("TestProfile")
                          ?? throw new InvalidOperationException("No test profile configured. Set TEST_PROFILE env var or TestProfile in appsettings.test.json.");

        var profileSection = builder.Configuration.GetSection($"TestProfiles:{profileName}");
        if (!profileSection.Exists())
            throw new InvalidOperationException($"Test profile '{profileName}' not found in TestProfiles.");

        builder.Services.Configure<TestRepoOptions>(profileSection);
        var provider = profileSection.GetValue<GitProvider>(nameof(TestRepoOptions.Provider));

        // Suppress per-request HttpClient log noise (traces still captured via OTEL instrumentation)
        builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

        // Infrastructure
        builder.AddOpenTelemetry();
        builder.Services.AddChatClients(builder.Configuration);
        builder.Services.AddCosmos(builder.Configuration);

        switch (provider)
        {
            case GitProvider.Ado:
                builder.Services.AddAdo();
                builder.Services.AddSingleton<IGitConnector>(sp => sp.GetRequiredKeyedService<IGitConnector>(GitProvider.Ado));
                builder.Services.AddSingleton<ITestHelper>(sp =>
                {
                    var testOpts = sp.GetRequiredService<IOptions<TestRepoOptions>>().Value;
                    var connector = (AdoConnector)sp.GetRequiredService<IGitConnector>();
                    var adoOrg = sp.GetRequiredService<IOptions<AdoOptions>>().Value.Organizations.Values.First();
                    return new AdoTestHelper(testOpts, connector._gitClients.GetOrAdd(adoOrg.Organization, _ =>
                    {
                        var connection = new Microsoft.VisualStudio.Services.WebApi.VssConnection(
                            new Uri($"https://dev.azure.com/{adoOrg.Organization}"),
                            new Microsoft.VisualStudio.Services.Common.VssBasicCredential(string.Empty, adoOrg.PersonalAccessToken));
                        return connection.GetClient<GitHttpClient>();
                    }));
                });
                break;

            case GitProvider.GitHub:
                builder.Services.AddGitHub();
                builder.Services.AddScoped<IGitConnector>(sp => sp.GetRequiredKeyedService<IGitConnector>(GitProvider.GitHub));
                builder.Services.AddSingleton<ITestHelper, GitHubTestHelper>();
                break;
        }

        // Domain
        builder.Services.AddSingleton<PrContextProvider>();
        builder.Services.AddSingleton(new FileAgentSkillsProvider(
            skillPath: Path.Combine(AppContext.BaseDirectory, "Skills")));
        builder.Services.AddKeyedScoped<IReviewStrategy, CoreStrategy>(ReviewStrategy.Core);
        builder.Services.AddScoped<Reviewer>(sp => new Reviewer(
            sp.GetRequiredKeyedService<IReviewStrategy>,
            sp.GetRequiredService<ICodeGraphStore>(),
            sp.GetRequiredService<IOptions<RevuOptions>>(),
            sp.GetRequiredService<ILogger<Reviewer>>(),
            sp.GetRequiredKeyedService<IChatClient>(ModelKey.Default),
            sp.GetRequiredService<ChatHistoryProvider>()));

        // Override Cosmos session provider — fresh session every run, captures to local JSON files.
        SessionDirectory = Path.Combine(AppContext.BaseDirectory, "sessions", $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        builder.Services.AddSingleton<ChatHistoryProvider>(new FileSessionProvider(SessionDirectory));

        _host = builder.Build();
    }

    public async Task InitializeAsync() => await _host.StartAsync();

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}
