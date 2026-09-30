using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Lexarbor.Host.Authentication;

/// <summary>
/// Internal only: the Confidential-client prepared logout request to the trusted
/// issuer, sent after the local administrator session has already been revoked and
/// its cookies cleared. Callers must pass the ID token from a revoked session
/// snapshot; this service never reads sessions, signs out, or enables a public mode.
/// It sends at most one POST without retry or redirect following, keeps the ID token
/// and client secret server-side, never surfaces upstream error bodies, and returns
/// only a <c>logout_uri</c> verified against the trusted issuer — or null, which
/// always means "local-only logout" and never a reason to retry.
/// </summary>
public sealed class AdminPreparedLogout(
    IHttpClientFactory clients, IOptions<OidcCodeOptions> codeOptions,
    IOptions<IdentityServiceOptions> identityOptions, IHostEnvironment environment)
{
    public const string BackchannelName = "LexarborPreparedLogout";
    /// <summary>The verified preparation answer is a small JSON object; anything larger is not one.</summary>
    public const int MaximumResponseBytes = 4 * 1024;
    public const string LogoutRequestPath = "/oauth2/logout/requests";
    public const string LogoutPath = "/oauth2/logout";
    public const string LogoutReturnPath = "/admin/auth/logout/return";

    /// <summary>
    /// The byte-exact post-logout redirect URI to send upstream, or null when none
    /// may be sent: not configured, or not an ASCII HTTPS URI (HTTP only on numeric
    /// loopback in Development/Testing) of this service's fixed anonymous return
    /// route with at most a registered static query. The value is never normalized;
    /// the provider matches it byte-for-byte against its registration.
    /// </summary>
    public string? ValidPostLogoutRedirectUri()
    {
        var value = codeOptions.Value.PostLogoutRedirectUri;
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Ascii(value, 1, 500) || !SafeUri(value, out var uri)) return null;
        if (!string.Equals(uri.AbsolutePath, LogoutReturnPath, StringComparison.Ordinal)) return null;
        if (uri.Scheme != "https" && !(IsLocalEnvironment() && uri.Scheme == "http"
            && uri.Host is "127.0.0.1" or "[::1]")) return null;
        // SignaCore appends the stored state; a registered URI that already carries
        // one, or repeats a field, would not survive its exact-match registration.
        var query = QueryHelpers.ParseQuery(uri.Query);
        return query.All(pair => !string.Equals(pair.Key, "state", StringComparison.OrdinalIgnoreCase)
            && pair.Value.Count == 1) ? value : null;
    }

    /// <summary>
    /// Prepares the upstream logout and returns the verified one-time logout URI the
    /// browser may be navigated to, or null for a local-only logout. A configured
    /// return redirect is sent only together with a one-time state; the pair is
    /// either complete or absent.
    /// </summary>
    public async Task<string?> RequestAsync(string idTokenHint, string? postLogoutRedirectUri, string? state,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // A compact ASCII JWS within the provider's 1–8192 bound. The hint comes from
        // an already validated sign-in; the provider revalidates it authoritatively
        // and ignores only its exp.
        if (!Ascii(idTokenHint, 1, 8192)) return null;
        if ((postLogoutRedirectUri is null) != (state is null)) return null;
        if (state is not null && !ProtocolValue(state, 22, 128)) return null;
        if (postLogoutRedirectUri is not null && (!Ascii(postLogoutRedirectUri, 1, 500)
            || !SafeUri(postLogoutRedirectUri, out _))) return null;
        var settings = codeOptions.Value;
        if (!Ascii(settings.ClientId, 1, 500) || !Ascii(settings.ClientSecret, 1, 500)) return null;
        if (!TryGetRequestEndpoint(out var endpoint, out var issuer)) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var form = new Dictionary<string, string>
            {
                ["client_id"] = settings.ClientId,
                ["client_secret"] = settings.ClientSecret,
                ["id_token_hint"] = idTokenHint
            };
            if (postLogoutRedirectUri is not null)
            {
                form["post_logout_redirect_uri"] = postLogoutRedirectUri;
                form["state"] = state!;
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new FormUrlEncodedContent(form)
            };
            using var response = await clients.CreateClient(BackchannelName).SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var bytes = await ReadBoundedAsync(response.Content, timeout.Token);
            // Failures are local JSON with fixed error codes upstream; none of it is
            // a reason to retry, echo, or undo the completed local revocation.
            if (bytes is null || !response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1)) return null;
            var logoutUri = root.TryGetProperty("logout_uri", out var property)
                && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
            return ValidLogoutUri(logoutUri, issuer);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // No raw exceptions, HTTP forms, or response bodies enter logs/results.
            return null;
        }
    }

    /// <summary>
    /// Only the trusted issuer's own completion endpoint may reach a browser: exact
    /// scheme/host/port, the fixed logout path, and a single canonical 43-character
    /// base64url <c>logout_handle</c> query — the one-time opaque handle is the sole
    /// value the browser is allowed to see. Anything else is discarded as local-only.
    /// </summary>
    private static string? ValidLogoutUri(string? value, Uri issuer)
    {
        if (value is null || !Ascii(value, 1, 500) || !SafeUri(value, out var uri)) return null;
        if (uri.Scheme != issuer.Scheme || !string.Equals(uri.Host, issuer.Host, StringComparison.Ordinal)
            || uri.Port != issuer.Port
            || !string.Equals(uri.AbsolutePath, LogoutPath, StringComparison.Ordinal)) return null;
        var query = QueryHelpers.ParseQuery(uri.Query);
        if (query.Count != 1 || !query.TryGetValue("logout_handle", out var handles)
            || handles.Count != 1 || !PendingAdminLogoutStore.Canonical(handles[0])) return null;
        return value;
    }

    private bool TryGetRequestEndpoint(out Uri endpoint, out Uri issuer)
    {
        // Discovery deliberately publishes no end_session_endpoint; the preparation
        // request goes to the fixed path on the same trusted issuer.
        endpoint = null!;
        issuer = null!;
        var value = identityOptions.Value.Issuer;
        if (!Ascii(value, 1, 500) || !SafeUri(value, out issuer) || issuer.Query.Length != 0) return false;
        if (issuer.Scheme != "https" && !(IsLocalEnvironment() && issuer.Scheme == "http")) return false;
        return Uri.TryCreate(value.TrimEnd('/') + LogoutRequestPath, UriKind.Absolute, out endpoint!);
    }

    private bool IsLocalEnvironment() => environment.IsDevelopment() || environment.IsEnvironment("Testing");
    private static bool SafeUri(string value, out Uri uri)
    {
        uri = null!;
        return Ascii(value, 1, 500) && !value.Contains('\\') && Uri.TryCreate(value, UriKind.Absolute, out uri!)
            && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.Host.Length > 0;
    }
    private static bool Ascii(string? value, int min, int max)
        => value is not null && value.Length >= min && value.Length <= max && value.All(c => c is >= '!' and <= '~');
    private static bool ProtocolValue(string value, int min, int max)
        => Ascii(value, min, max) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~');
    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes) return null;
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) return bytes.ToArray();
            if (bytes.Length + read > MaximumResponseBytes) return null;
            bytes.Write(buffer, 0, read);
        }
    }
}
