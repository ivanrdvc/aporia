using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

using Aporia.Infra.Telemetry;

namespace Aporia.Cli;

internal static class CliExtensions
{
    /// <summary>
    /// Adds OpenTelemetry tracing and metrics without Azure Functions or Azure Monitor dependencies.
    /// Exports to OTLP if OTEL_EXPORTER_OTLP_ENDPOINT is set, otherwise just logs locally.
    /// </summary>
    public static void AddCliTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddLogging(logging => logging.AddOpenTelemetry(otel =>
        {
            otel.IncludeFormattedMessage = true;
            otel.IncludeScopes = true;
        }));

        var otel = services
            .AddOpenTelemetry()
            .WithTracing(tracing => tracing
                .AddSource(Telemetry.ServiceName)
                .AddSource("Experimental.Microsoft.Agents.AI")
                .AddSource("Microsoft.Extensions.AI.*")
                .AddProcessor<TokenSummaryProcessor>()
                .AddHttpClientInstrumentation())
            .WithMetrics(metrics => metrics
                .AddMeter(Telemetry.ServiceName)
                .AddMeter("Microsoft.Agents.AI*")
                .AddMeter("Microsoft.Extensions.AI*")
                .AddHttpClientInstrumentation());

        if (configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] is not null)
            otel.UseOtlpExporter();
    }
}
