using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

using Aporia.Infra.Telemetry;

namespace Aporia.Cli;

internal static class CliExtensions
{
    // UseOtlpExporter() reads config from env vars. Azure Functions surfaces appsettings as env
    // vars automatically; generic host does not, so we bridge the OTEL keys here.
    private static readonly string[] OtelEnvKeys =
    [
        "OTEL_EXPORTER_OTLP_ENDPOINT",
        "OTEL_EXPORTER_OTLP_PROTOCOL",
        "OTEL_EXPORTER_OTLP_HEADERS",
        "OTEL_SERVICE_NAME",
    ];

    public static void AddCliTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        foreach (var key in OtelEnvKeys)
        {
            if (configuration[key] is { } value)
                Environment.SetEnvironmentVariable(key, value);
        }

        services.AddLogging(logging => logging.AddOpenTelemetry(otel =>
        {
            otel.IncludeFormattedMessage = true;
            otel.IncludeScopes = true;
        }));

        var otel = services
            .AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(configuration["OTEL_SERVICE_NAME"] ?? "Aporia.Cli"))
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
