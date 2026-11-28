namespace Lexarbor.Learning.Service;

internal static class ApiKeyRules
{
    internal const string HeaderName = "X-Api-Key";

    internal static bool IsValid(string value) =>
        value.Length > 0 && value.All(character => character is >= '!' and <= '~' and not ',');

    internal static bool IsAllowedEndpoint(string? value, bool allowInsecureHttp) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Host.Length > 0
        && (uri.Scheme == Uri.UriSchemeHttps || (allowInsecureHttp && uri.Scheme == Uri.UriSchemeHttp))
        && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;
}
