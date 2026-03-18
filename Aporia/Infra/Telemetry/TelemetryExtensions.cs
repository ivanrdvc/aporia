using Azure.Monitor.OpenTelemetry.Exporter;

using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Aporia.Infra.Telemetry;

public static class TelemetryExtensions
{
    public static void AddOpenTelemetry(this IHostApplicationBuilder builder)
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        var otel = builder.Services
            .AddOpenTelemetry()
            .UseFunctionsWorkerDefaults()
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

        if (builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] is not null)
            otel.UseOtlpExporter();

        if (builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"] is not null)
            otel.UseAzureMonitorExporter();
    }
}
