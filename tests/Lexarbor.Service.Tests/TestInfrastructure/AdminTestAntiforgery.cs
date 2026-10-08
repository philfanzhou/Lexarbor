using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Lexarbor.Host;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Lexarbor.Service.Tests.TestInfrastructure;

/// <summary>
/// The hosted-login antiforgery pair for tests that exercise session-authenticated unsafe
/// methods: one fetch of <c>GET /admin/auth/csrf</c> per test host (the pair is bound to
/// the host's own antiforgery key ring and to the browser cookie issued alongside), then
/// the token travels in the <c>X-SignaCore-CSRF</c> request header and the cookie joins
/// the request's Cookie header — replacing the retired <c>X-Requested-With</c> marker.
/// </summary>
public static class AdminTestAntiforgery
{
    public const string HeaderName = "X-SignaCore-CSRF";

    private static readonly ConditionalWeakTable<WebApplicationFactory<Program>, Pair> Cache = new();

    private sealed class Pair(string token, string cookie)
    {
        public string Token { get; } = token;
        public string Cookie { get; } = cookie;
    }

    public static (string Token, string Cookie) Get(WebApplicationFactory<Program> factory) =>
        Cache.GetValue(factory, static host =>
        {
            // The antiforgery pair is issued with a Secure cookie: the probe client
            // must present an https origin, which the test host serves like any other.
            using var client = host.CreateClient(new WebApplicationFactoryClientOptions
            { BaseAddress = new Uri("https://localhost") });
            using var response = client.GetAsync("/admin/auth/csrf",
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(response.Content.ReadAsStream(
                TestContext.Current.CancellationToken));
            var token = body.RootElement.GetProperty("token").GetString()
                ?? throw new InvalidOperationException("The csrf endpoint answered without a token.");
            var cookie = response.Headers.GetValues("Set-Cookie").Single()
                .Split(';')[0];
            return new Pair(token, cookie);
        }) is { } pair ? (pair.Token, pair.Cookie) : throw new InvalidOperationException();

    /// <summary>
    /// Adds the token header and merges the antiforgery cookie into the client's
    /// request cookies — the drop-in replacement for the retired global
    /// X-Requested-With header. The merge keeps one single Cookie header value, the
    /// shape every browser sends.
    /// </summary>
    public static void Attach(HttpClient client, WebApplicationFactory<Program> factory)
    {
        var (token, cookie) = Get(factory);
        client.DefaultRequestHeaders.Add(HeaderName, token);
        List<string> cookies = [];
        if (client.DefaultRequestHeaders.TryGetValues("Cookie", out var existing))
        {
            cookies.AddRange(existing);
            client.DefaultRequestHeaders.Remove("Cookie");
        }
        cookies.Add(cookie);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", string.Join("; ", cookies));
    }
}
