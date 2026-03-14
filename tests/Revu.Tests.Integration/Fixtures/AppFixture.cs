using Microsoft.Agents.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
        builder.Services.Configure<TestRepoOptions>(builder.Configuration.GetSection(TestRepoOptions.SectionName));

        // Suppress per-request HttpClient log noise (traces still captured via OTEL instrumentation)
        builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

        // Infrastructure
        builder.AddOpenTelemetry();
        builder.Services.AddChatClients(builder.Configuration);
        builder.Services.AddCosmos(builder.Configuration);
        builder.Services.AddAdoClient();

        // Domain
        builder.Services.AddSingleton<PrContextProvider>();
        builder.Services.AddSingleton(new FileAgentSkillsProvider(
            skillPath: Path.Combine(AppContext.BaseDirectory, "Skills")));
        builder.Services.AddSingleton<IGitConnector, AdoConnector>();
        builder.Services.AddKeyedScoped<IReviewStrategy, CoreStrategy>(ReviewStrategy.Core);
        builder.Services.AddScoped<Reviewer>(sp => new Reviewer(
            sp.GetRequiredKeyedService<IReviewStrategy>,
            sp.GetRequiredService<ICodeGraphStore>(),
            sp.GetRequiredService<IOptions<RevuOptions>>(),
            sp.GetRequiredService<ILogger<Reviewer>>()));

        // Override Cosmos session provider — fresh session every run, captures to local JSON files.
        SessionDirectory = Path.Combine(AppContext.BaseDirectory, "sessions", $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        builder.Services.AddSingleton<ChatHistoryProvider>(new FileSessionProvider(SessionDirectory));

        _host = builder.Build();

        AdoThreadHelper.Initialize(_host.Services.GetRequiredService<IOptions<TestRepoOptions>>());
    }

    public async Task InitializeAsync() => await _host.StartAsync();

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}
