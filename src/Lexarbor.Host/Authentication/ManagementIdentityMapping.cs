using System.Security.Claims;
using ServiceMantle.Audit;
using ServiceMantle.Management;

namespace Lexarbor.Host.Authentication;

/// <summary>
/// Produces the ServiceMantle management identity on a validated administrator
/// principal: a principal that carries the configured administrator role gets
/// <c>servicemantle.operator_id</c> (the verified <c>sub</c>),
/// <c>servicemantle.operator_source</c> and the <c>management.admin</c>
/// permission on its single authenticated identity, and nothing else changes.
/// </summary>
/// <remarks>
/// <para>
/// The ServiceMantle identity is minted here and only here. Any
/// <c>servicemantle.*</c> claim an inbound credential carried is removed from
/// every identity before the mapping runs, so a client-injected operator id,
/// source or permission can never survive into authorization — whether or not
/// the role check then passes.
/// </para>
/// <para>
/// The mapping adds claims verbatim and keeps the ServiceMantle parser the
/// single validation authority: a <c>sub</c> that is not already in the
/// operator-id wire form (blank, padded, control characters, or over long)
/// produces claims the parser refuses, so the <c>ServiceMantle.ManagementAdmin</c>
/// policy fails closed with 403 rather than truncating or rewriting the
/// subject. A principal without the required role gets no ServiceMantle claims
/// at all and fails the same policy the same way.
/// </para>
/// </remarks>
public static class ManagementIdentityMapping
{
    public static void Apply(ClaimsPrincipal? principal, string? requiredRole, string operatorSource)
    {
        if (principal is null)
        {
            return;
        }

        foreach (var identity in principal.Identities)
        {
            foreach (var claim in identity.Claims.Where(IsManagementClaim).ToList())
            {
                identity.TryRemoveClaim(claim);
            }
        }

        if (string.IsNullOrWhiteSpace(requiredRole)
            || !VocabularyClaims.HasRole(principal, requiredRole))
        {
            return;
        }

        // The subject is resolved through the same claim-type chain as every
        // other identity read (short "sub" or the ClaimTypes URI), so both
        // issuer shapes project.
        var subject = VocabularyClaims.GetSubject(principal);
        if (string.IsNullOrEmpty(subject))
        {
            // No subject to project: leave the principal without a management
            // identity so the policy fails closed instead of inventing one.
            return;
        }

        var authenticated = principal.Identities.SingleOrDefault(item => item.IsAuthenticated);
        if (authenticated is null)
        {
            return;
        }

        authenticated.AddClaim(new Claim(ManagementClaimTypes.OperatorId, subject));
        authenticated.AddClaim(new Claim(ManagementClaimTypes.OperatorSource, operatorSource));
        authenticated.AddClaim(new Claim(ManagementClaimTypes.Permission, ManagementPermissions.AdminValue));
    }

    private static bool IsManagementClaim(Claim claim) =>
        claim.Type.StartsWith("servicemantle.", StringComparison.Ordinal);
}
