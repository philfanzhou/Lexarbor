using Lexarbor.Host;
using OpenTelemetry.Trace;
using ServiceMantle.Web;

public partial class Program
{
    /// <summary>
    /// Optional OpenTelemetry, default off: with neither signal enabled nothing
    /// is registered — no provider, no exporter, no outbound connection — and the
    /// process behaves exactly as before. The resource carries only the ServiceMantle
    /// identity (service.name, service.version, service.instance.id).
    /// </summary>
    private static void AddTelemetry(WebApplicationBuilder builder, ServiceMantleBuilder serviceMantle)
    {
        var telemetryTracesEnabled = builder.Configuration.GetValue("Telemetry:Otlp:Traces:Enabled", false);
        var telemetryMetricsEnabled = builder.Configuration.GetValue("Telemetry:Otlp:Metrics:Enabled", false);
        if (telemetryTracesEnabled || telemetryMetricsEnabled)
        {
            serviceMantle.AddOpenTelemetryInstrumentation(options =>
            {
                options.EnableAspNetCoreTracing = telemetryTracesEnabled;
                options.EnableHttpClientTracing = telemetryTracesEnabled;
                options.EnableRuntimeMetrics = telemetryMetricsEnabled;
            });
            serviceMantle.AddOpenTelemetryOtlpExporter(options =>
            {
                MapOtlpSignal(builder.Configuration.GetSection("Telemetry:Otlp:Traces"), options.Traces);
                MapOtlpSignal(builder.Configuration.GetSection("Telemetry:Otlp:Metrics"), options.Metrics);
                options.Traces.Enabled = telemetryTracesEnabled;
                options.Metrics.Enabled = telemetryMetricsEnabled;
            });
            if (telemetryTracesEnabled)
            {
                // The callback and logout-return URLs carry one-time code/state values;
                // no span attribute may export them.
                builder.Services.ConfigureOpenTelemetryTracerProvider((_, tracing) =>
                    tracing.AddProcessor(new AdminCallbackQueryRedactor()));
            }

            // The header name is configuration; the value comes from the environment
            // only — read once, at construction, into one shared fixed resolver per
            // configured signal — and never reaches logs or exceptions. With the
            // environment value absent every query answers false, which the runtime
            // reports as its authentication_missing startup failure.
            builder.Services.AddSingleton<ServiceMantle.Diagnostics.IRemoteTelemetryAuthenticationResolver>(
                new OtlpAuthenticationResolver(
                [
                    builder.Configuration["Telemetry:Otlp:Traces:AuthenticationHeaderName"],
                builder.Configuration["Telemetry:Otlp:Metrics:AuthenticationHeaderName"]
                ]));
        }
    }

    private static void MapOtlpSignal(IConfiguration section, ServiceMantle.Diagnostics.Export.Otlp.OtlpSignalOptions signal)
    {
        if (Uri.TryCreate(section["Endpoint"], UriKind.Absolute, out var endpoint))
        {
            signal.Endpoint = endpoint;
        }

        // The transport scheme is the deployment's decision since ServiceMantle
        // 0.3.2: http and https endpoints are both accepted, and the retired
        // AllowInsecureLoopbackForTesting key is no longer read.
        signal.AuthenticationHeaderName = section["AuthenticationHeaderName"];
        if (Enum.TryParse<ServiceMantle.Diagnostics.Export.Otlp.OtlpProtocol>(section["Protocol"], ignoreCase: true, out var protocol))
        {
            signal.Protocol = protocol;
        }
    }
}
