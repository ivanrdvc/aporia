using Microsoft.Agents.AI;
using Microsoft.Azure.Functions.Worker.Builder;
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

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services.AddOptions<RevuOptions>().BindConfiguration(RevuOptions.SectionName);

// Infrastructure
builder.AddOpenTelemetry();
builder.Services.AddChatClients(builder.Configuration);
builder.Services.AddCosmos(builder.Configuration);
builder.Services.AddAdoClient();
builder.Services.AddSingleton(new FileAgentSkillsProvider(skillPath: Path.Combine(AppContext.BaseDirectory, "Skills")));
builder.Services.AddSingleton<PrContextProvider>();

// Domain
builder.Services.AddSingleton<IGitConnector, AdoConnector>();
builder.Services.AddKeyedScoped<IReviewStrategy, CoreStrategy>(ReviewStrategy.Core);
builder.Services.AddScoped<Reviewer>(sp => new Reviewer(
    sp.GetRequiredKeyedService<IReviewStrategy>,
    sp.GetRequiredService<ICodeGraphStore>(),
    sp.GetRequiredService<IOptions<RevuOptions>>(),
    sp.GetRequiredService<ILogger<Reviewer>>()));

builder.Build().Run();
