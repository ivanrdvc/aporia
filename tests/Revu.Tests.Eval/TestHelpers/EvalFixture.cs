using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Revu.Git;
using Revu.Infra.AI;
using Revu.Infra.Telemetry;
using Revu.Review;
using Revu.Tests.Eval.Evaluators;

namespace Revu.Tests.Eval.TestHelpers;

public class EvalFixture : IAsyncLifetime
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly IEvaluator[] s_evaluators =
    [
        new FindingGroundednessEvaluator(),
        new ExpectedFindingsEvaluator(),
        new AgentBehaviorEvaluator(),
        new FindingQualityEvaluator()
    ];

    private readonly IHost _host;

    public EvalFixture()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });

        builder.Configuration
            .AddJsonFile("appsettings.test.json", optional: true)
            .AddUserSecrets<EvalFixture>()
            .AddEnvironmentVariables();

        builder.Services.Configure<AIOptions>(builder.Configuration.GetSection(AIOptions.SectionName));

        builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

        builder.AddOpenTelemetry();
        builder.Services.AddChatClients(builder.Configuration);

        _host = builder.Build();

        Reporting = DiskBasedReportingConfiguration.Create(
            storageRootPath: Path.Combine(AppContext.BaseDirectory, "EvalResults"),
            evaluators: s_evaluators,
            chatConfiguration: new ChatConfiguration(JudgeClient),
            enableResponseCaching: true,
            executionName: DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
    }

    /// <summary>The default model client, reused as the LLM judge for quality evaluators.</summary>
    public IChatClient JudgeClient =>
        _host.Services.GetRequiredKeyedService<IChatClient>(ModelKey.Default);

    /// <summary>Reporting config with disk-backed result storage and response caching.</summary>
    public ReportingConfiguration Reporting { get; }

    /// <summary>All metric names across all evaluators, for result logging.</summary>
    public IEnumerable<string> MetricNames => s_evaluators.SelectMany(e => e.EvaluationMetricNames);

    public (Reviewer Reviewer, CapturingChatClient Capture) CreateReviewer(IGitConnector git)
    {
        var capture = new CapturingChatClient(
            _host.Services.GetRequiredKeyedService<IChatClient>(ModelKey.Reasoning));

        var reviewer = new Reviewer(
            _ => new CoreStrategy(
                capture,
                _host.Services.GetRequiredKeyedService<IChatClient>(ModelKey.Default),
                git,
                new NullSessionProvider(),
                new FileAgentSkillsProvider(skillPath: Path.Combine(AppContext.BaseDirectory, "Skills")),
                new PrContextProvider(),
                NullLogger<CoreStrategy>.Instance),
            NullLogger<Reviewer>.Instance);

        return (reviewer, capture);
    }

    public static FixtureData LoadFixture(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", fileName);
        return JsonSerializer.Deserialize<FixtureData>(File.ReadAllText(path), s_jsonOptions)
               ?? throw new InvalidOperationException($"Failed to deserialize fixture: {fileName}");
    }

    public Task InitializeAsync() => _host.StartAsync();

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private sealed class NullSessionProvider : ChatHistoryProvider
    {
        protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
            InvokingContext context, CancellationToken cancellationToken = default) => new([]);

        protected override ValueTask StoreChatHistoryAsync(
            InvokedContext context, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}

public record FixtureData(
    ReviewRequest Request,
    ProjectConfig Config,
    Diff Diff,
    Dictionary<string, string> Files,
    FixtureExpectations? Expectations = null
);

public record FixtureExpectations(
    string? ExpectedMode = null,
    List<ExpectedFinding>? ExpectedFindings = null,
    bool ExpectNoFindings = false
);

public record ExpectedFinding(string File, string Keyword, string Description, bool Required = true);
