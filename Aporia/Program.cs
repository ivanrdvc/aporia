using Microsoft.Agents.AI;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Aporia.Git;
using Aporia.Infra;
using Aporia.Infra.AI;
using Aporia.Infra.Cosmos;
using Aporia.Infra.Telemetry;
using Aporia.Review;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services.AddOptions<AporiaOptions>().BindConfiguration(AporiaOptions.SectionName);

// Infrastructure
builder.AddOpenTelemetry();
builder.Services.AddChatClients(builder.Configuration);
builder.Services.AddCosmos(builder.Configuration);
builder.Services.AddGitHub();
builder.Services.AddAdo();
builder.Services.AddSingleton(new FileAgentSkillsProvider(skillPath: Path.Combine(AppContext.BaseDirectory, "Skills")));
builder.Services.AddSingleton<PrContextProvider>();

// Domain
builder.Services.AddKeyedScoped<IReviewStrategy, CoreStrategy>(ReviewStrategy.Core);
builder.Services.AddScoped<Reviewer>(sp => new Reviewer(
    sp.GetRequiredKeyedService<IReviewStrategy>,
    sp.GetRequiredService<ICodeGraphStore>(),
    sp.GetRequiredService<IOptions<AporiaOptions>>(),
    sp.GetRequiredService<ILogger<Reviewer>>(),
    sp.GetRequiredKeyedService<IChatClient>(ModelKey.Default),
    sp.GetRequiredService<ChatHistoryProvider>()));

builder.Build().Run();
