namespace Lexarbor.Service;

/// <summary>
/// The single Service-layer carrier of the administrator authorization policy
/// name.
/// </summary>
/// <remarks>
/// <see cref="Lexarbor.Service"/> does not reference ServiceMantle.Web, so the
/// name cannot come from <c>ManagementAuthorizationDefaults</c> here; the Host
/// uses the library constant directly. The two are pinned together by an
/// equality test, so a rename on either side fails the build's test run rather
/// than silently leaving the administration routes unprotected.
/// </remarks>
public static class AdminEndpointAuthorization
{
    /// <summary>
    /// The ServiceMantle management-admin policy that protects every
    /// <c>/admin/*</c> route Lexarbor maps.
    /// </summary>
    public const string PolicyName = "ServiceMantle.ManagementAdmin";
}
