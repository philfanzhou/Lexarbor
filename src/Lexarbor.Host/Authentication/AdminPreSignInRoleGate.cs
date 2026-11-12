using Microsoft.Extensions.Options;

namespace Lexarbor.Host.Authentication;

/// <summary>
/// The pre-sign-in administrator gate (#209's original extension point), carried over from the
/// package's <c>PreSignInAuthorizationDecision</c> seam to the framework handler's
/// token-validated event: only a subject whose independently verified access token carries the
/// configured administrator role may establish a session. The SignaCore ID token is a closed
/// set without roles, so the gate's input is deliberately the access-token principal; the
/// session store re-verifies the same boundary again at storage time.
/// </summary>
public sealed class AdminPreSignInRoleGate(IOptionsMonitor<AdminAuthenticationOptions> options)
{
    public bool AllowsSession(System.Security.Claims.ClaimsPrincipal accessTokenPrincipal)
    {
        return VocabularyClaims.HasRole(accessTokenPrincipal, options.CurrentValue.RequiredRole);
    }
}
