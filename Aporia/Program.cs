using Microsoft.Agents.AI;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Aporia.DocWatch;
using Aporia.Git;
using Aporia.Infra;
using Aporia.Infra.AI;
using Aporia.Infra.Telemetry;
using Aporia.Review;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services.AddOptions<AporiaOptions>().BindConfiguration(AporiaOptions.SectionName);

// Infrastructure
builder.Services.AddOpenTelemetry(builder.Configuration);
builder.Services.AddChatClients(builder.Configuration);
builder.Services.AddCosmos(builder.Configuration);
builder.Services.AddGitHub();
builder.Services.AddAdo();
builder.Services.AddSingleton(new FileAgentSkillsProvider(skillPath: Path.Combine(AppContext.BaseDirectory, "Skills")));
builder.Services.AddSingleton<PrContextProvider>();

// Domain
builder.Services.AddKeyedScoped<IReviewStrategy, CoreStrategy>(ReviewStrategy.Core);
builder.Services.AddKeyedScoped<IReviewStrategy, CopilotStrategy>(ReviewStrategy.Copilot);
builder.Services.AddScoped<Func<string, IReviewStrategy>>(sp => key => sp.GetRequiredKeyedService<IReviewStrategy>(key));
builder.Services.AddScoped<Reviewer>();
builder.Services.AddScoped<DocWatcher>();

builder.Build().Run();
