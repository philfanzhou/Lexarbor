using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Lexarbor.Service.Tests;

/// <summary>
/// The optional ServiceMantle OpenTelemetry integration: nothing is registered
/// by default; enabling traces and metrics exports through OTLP with only the
/// three service identity resource attributes; the transport scheme is the
/// deployment's decision while structurally unsafe endpoints refuse startup;
/// the authentication behavior keeps its three-branch matrix over the shared
/// fixed resolvers; and the hosted-login callback and logout-return spans never
/// carry code or state.
/// </summary>
public class TelemetryExportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Telemetry configuration is read before the test host's in-memory sources
    /// attach, so tests enable it through the factory's locked
    /// environment-variable passthrough — the same path a deployment's
    /// container variables take.
    /// </summary>
    private static WebApplicationFactory<Program> TelemetryFactory(
        LoopbackCollector? collector,
        Action<Dictionary<string, string?>>? extendConfiguration = null,
        Action<Microsoft.AspNetCore.Hosting.IWebHostBuilder>? configureHost = null)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["Telemetry:Otlp:Traces:Enabled"] = "true",
            ["Telemetry:Otlp:Traces:Protocol"] = "HttpProtobuf"
        };
        if (collector is not null)
        {
            configuration["Telemetry:Otlp:Traces:Endpoint"] = collector.BaseAddress + "v1/traces";
            configuration["Telemetry:Otlp:Metrics:Enabled"] = "true";
            configuration["Telemetry:Otlp:Metrics:Endpoint"] = collector.BaseAddress + "v1/metrics";
            configuration["Telemetry:Otlp:Metrics:Protocol"] = "HttpProtobuf";
        }
        else
        {
            configuration["Telemetry:Otlp:Traces:Endpoint"] = "https://collector.example";
        }

        extendConfiguration?.Invoke(configuration);
        var factory = new VocabularyWebApplicationFactory("Testing", true,
            extraConfiguration: configuration);
        return configureHost is null ? factory : factory.WithWebHostBuilder(configureHost);
    }

    [Fact]
    public async Task DefaultConfiguration_RegistersNoProvidersAndStaysIdle()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();

        Assert.Null(factory.Services.GetService<TracerProvider>());
        Assert.Null(factory.Services.GetService<MeterProvider>());
        using var response = await client.GetAsync("/health", Ct);
        Assert.True(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task EnabledTracesAndMetrics_ExportWithOnlyIdentityResourceAttributes()
    {
        using var collector = new LoopbackCollector();
        await using var factory = TelemetryFactory(collector);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health", Ct);
        Assert.True(response.IsSuccessStatusCode);
        await collector.WaitAsync();

        Assert.True(collector.TraceBytes > 0, "the collector received no trace export");
        Assert.True(collector.MetricBytes > 0, "the collector received no metric export");
    }

    [Fact]
    public async Task ConfiguredHeaderNamesWithEnvironmentValue_SendEachSignalItsHeader()
    {
        // Branch two of the authentication matrix, and the reason the
        // dispatching composite cannot be collapsed into one shared resolver:
        // each signal may carry a different header name, and each query name
        // must answer with the environment value under that exact name.
        using var collector = new LoopbackCollector();
        await using var factory = TelemetryFactory(collector, configuration =>
        {
            configuration["Telemetry:Otlp:Traces:AuthenticationHeaderName"] = "Authorization";
            configuration["Telemetry:Otlp:Metrics:AuthenticationHeaderName"] = "X-Metrics-Token";
            configuration["LEXARBOR_TELEMETRY_OTLP_AUTHORIZATION"] = "collector-secret-value";
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health", Ct);
        Assert.True(response.IsSuccessStatusCode);
        await collector.WaitAsync();

        Assert.Contains(collector.Requests, request =>
            request.TryGetValue("Authorization", out var value)
            && value == "collector-secret-value");
        Assert.Contains(collector.Requests, request =>
            request.TryGetValue("X-Metrics-Token", out var value)
            && value == "collector-secret-value");
    }

    [Fact]
    public async Task NoHeaderNameConfigured_ExportsWithoutAuthenticationHeader()
    {
        // Branch one of the authentication matrix: without a configured header
        // name nothing is queried, and a present environment value alone never
        // adds a header to the exports.
        using var collector = new LoopbackCollector();
        await using var factory = TelemetryFactory(collector, configuration =>
        {
            configuration["LEXARBOR_TELEMETRY_OTLP_AUTHORIZATION"] = "collector-secret-value";
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health", Ct);
        Assert.True(response.IsSuccessStatusCode);
        await collector.WaitAsync();

        Assert.DoesNotContain(collector.Requests, request => request.ContainsKey("Authorization"));
    }

    [Fact]
    public void ConfiguredHeaderNameWithoutEnvironmentValue_FailsStartupClosed()
    {
        // Branch three of the authentication matrix: a configured header name
        // whose environment value is absent refuses startup — never a silent
        // export without authentication.
        Exception? failure = null;
        try
        {
            using var factory = TelemetryFactory(null, configuration =>
            {
                configuration["Telemetry:Otlp:Traces:AuthenticationHeaderName"] = "Authorization";
                configuration["LEXARBOR_TELEMETRY_OTLP_AUTHORIZATION"] = null;
            });
            using var client = factory.CreateClient();
        }
        catch (Exception caught)
        {
            failure = caught;
        }

        Assert.NotNull(failure);
        Assert.Contains("otlp.authentication_missing", failure!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PlainHttpEndpoint_IsAcceptedAsADeploymentDecision()
    {
        // ServiceMantle 0.3.2 made the transport scheme the deployment's
        // decision: http and https endpoints are both accepted, so a
        // plain-HTTP collector endpoint no longer stops startup. Whether it
        // should be used is the operator's call, not the library's.
        using var factory = TelemetryFactory(null, configuration =>
        {
            configuration["Telemetry:Otlp:Traces:Endpoint"] = "http://collector.insecure.example";
        });
        using var client = factory.CreateClient();
    }

    [Fact]
    public void EndpointWithUnsafeComponents_RefusesStartupWithoutEchoingTheEndpoint()
    {
        Exception? failure = null;
        try
        {
            using var factory = TelemetryFactory(null, configuration =>
            {
                configuration["Telemetry:Otlp:Traces:Endpoint"] = "https://collector.example?ticket=secret";
            });
            using var client = factory.CreateClient();
        }
        catch (Exception caught)
        {
            failure = caught;
        }

        // The structural URI rules are unchanged: query, fragment, and
        // user-info components refuse startup, and the fixed diagnostic
        // carries no endpoint text.
        Assert.NotNull(failure);
        Assert.DoesNotContain("collector.example", failure!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallbackAndLogoutReturnSpans_NeverCarryCodeOrState()
    {
        var spans = new CapturedSpans();
        await using var host = TelemetryFactory(null, configureHost: builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Capture spans at the SDK level; the exporter cannot reach the
                // configured endpoint, which does not affect the attributes.
                services.ConfigureOpenTelemetryTracerProvider((_, tracing) =>
                    tracing.AddProcessor(spans));
            });
        });
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        using (var callback = await client.GetAsync(
            "/admin/auth/callback?state=sensitive-state-marker&iss=https%3A%2F%2Fissuer.test&code=sensitive-code-marker",
            Ct))
        {
            Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        }

        using (var logoutReturn = await client.GetAsync(
            "/admin/auth/logout/return?state=sensitive-logout-state-marker",
            Ct))
        {
            Assert.Equal(HttpStatusCode.Redirect, logoutReturn.StatusCode);
        }

        var relevant = spans.Activities.Where(activity =>
            (activity.GetTagItem("url.path") as string ?? activity.GetTagItem("http.route") as string)?
                .TrimEnd('/') is "/admin/auth/callback" or "/admin/auth/logout/return")
            .ToArray();
        Assert.NotEmpty(relevant);
        foreach (var activity in relevant)
        {
            foreach (var tag in activity.TagObjects)
            {
                var value = tag.Value?.ToString();
                Assert.DoesNotContain("sensitive-state-marker", value, StringComparison.Ordinal);
                Assert.DoesNotContain("sensitive-code-marker", value, StringComparison.Ordinal);
                Assert.DoesNotContain("sensitive-logout-state-marker", value, StringComparison.Ordinal);
            }

            // The redaction processor replaced any query-bearing attribute.
            Assert.DoesNotContain(activity.TagObjects,
                tag => tag.Key is "url.query" or "url.full" or "http.url" or "http.target"
                    && tag.Value?.ToString() != "[REDACTED]");
        }
    }

    private sealed class CapturedSpans : OpenTelemetry.BaseProcessor<Activity>
    {
        public List<Activity> Activities { get; } = [];

        public override void OnEnd(Activity activity)
        {
            Activities.Add(activity);
        }
    }

    /// <summary>
    /// Receives OTLP HTTP/protobuf POSTs on loopback. Only the fact, size, and
    /// header names/values of the export are asserted; the payload stays
    /// opaque bytes.
    /// </summary>
    private sealed class LoopbackCollector : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _server;
        private readonly ConcurrentBag<IReadOnlyDictionary<string, string>> _requests = [];
        private long _traceBytes;
        private long _metricBytes;

        /// <summary>Every received request's headers, name/value pairs.</summary>
        public IReadOnlyList<IReadOnlyDictionary<string, string>> Requests =>
            [.. _requests];

        public LoopbackCollector()
        {
            var port = Random.Shared.Next(20000, 29000);
            BaseAddress = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(BaseAddress);
            _listener.Start();
            _server = Task.Run(async () =>
            {
                try
                {
                    while (_listener.IsListening)
                    {
                        var context = await _listener.GetContextAsync();
                        using var body = context.Request.InputStream;
                        var bytes = await body.ReadAsync(new byte[context.Request.ContentLength64 > 0
                            ? context.Request.ContentLength64
                            : 8192], Ct);
                        var total = bytes;
                        while (bytes > 0)
                        {
                            bytes = await body.ReadAsync(new byte[8192], Ct);
                            total += bytes;
                        }

                        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var key in context.Request.Headers.AllKeys)
                        {
                            if (key is not null)
                            {
                                headers[key] = context.Request.Headers[key] ?? string.Empty;
                            }
                        }

                        _requests.Add(headers);

                        if (context.Request.Url?.AbsolutePath.Contains("traces", StringComparison.Ordinal) == true)
                        {
                            Interlocked.Add(ref _traceBytes, total);
                        }
                        else if (context.Request.Url?.AbsolutePath.Contains("metrics", StringComparison.Ordinal) == true)
                        {
                            Interlocked.Add(ref _metricBytes, total);
                        }
                        else
                        {
                            Interlocked.Add(ref _traceBytes, total);
                        }

                        context.Response.StatusCode = 200;
                        context.Response.Close();
                    }
                }
                catch (Exception exception) when (
                    exception is ObjectDisposedException or HttpListenerException)
                {
                    // Listener stopped during shutdown.
                }
            });
        }

        public string BaseAddress { get; }

        public long TraceBytes => Interlocked.Read(ref _traceBytes);
        public long MetricBytes => Interlocked.Read(ref _metricBytes);

        public async Task WaitAsync()
        {
            for (var attempt = 0; attempt < 60; attempt++)
            {
                if (TraceBytes > 0 && MetricBytes > 0)
                {
                    return;
                }

                await Task.Delay(500, Ct);
            }
        }

        public void Dispose()
        {
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                _server.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }
        }
    }

    private sealed class ConsoleCapture : IDisposable
    {
        private readonly TextWriter _original = Console.Out;
        private readonly StringWriter _writer = new();

        public ConsoleCapture() => Console.SetOut(_writer);

        public string Output
        {
            get
            {
                _writer.Flush();
                return _writer.ToString();
            }
        }

        public void Dispose()
        {
            Console.SetOut(_original);
            _writer.Dispose();
        }
    }
}
