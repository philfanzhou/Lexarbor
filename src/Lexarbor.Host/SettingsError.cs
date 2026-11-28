namespace Lexarbor.Host;

/// <summary>The failure shape shared by the settings endpoints: { "message": "..." }.</summary>
internal static class SettingsError
{
    public static IResult Write(int status, string message) => Results.Json(new { message }, statusCode: status);
}
