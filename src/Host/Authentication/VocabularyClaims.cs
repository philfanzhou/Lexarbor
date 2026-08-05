using System.Security.Claims;

namespace Ruoyu.Study.Vocabulary.Host.Authentication;

/// <summary>
/// Reads identity information from a validated <see cref="ClaimsPrincipal"/>.
///
/// QuantumZhou.Identity builds its JWT payload directly (<c>new JwtPayload(...)</c>),
/// which bypasses the outbound claim type map, so roles arrive as the full
/// <see cref="ClaimTypes.Role"/> URI rather than the short "role" name. Standard OIDC
/// providers emit the short names instead. Both shapes are accepted so the service is
/// not tied to one issuer's serialization choice.
///
/// Mirrors the convention already used by DocLibrary and
/// <c>Ruoyu.Study.Common.Authentication.ClaimsPrincipalExtensions</c>.
/// </summary>
public static class VocabularyClaims
{
    private static readonly string[] RoleClaimTypes = ["role", ClaimTypes.Role];

    private static readonly string[] NameClaimTypes =
    [
        ClaimTypes.Name,
        "preferred_username",
        "unique_name",
        "nickname"
    ];

    private static readonly string[] SubjectClaimTypes =
    [
        ClaimTypes.NameIdentifier,
        "sub",
        "nameid"
    ];

    public static IReadOnlyList<string> GetRoles(ClaimsPrincipal? user)
    {
        if (user == null)
        {
            return [];
        }

        return user.Claims
            .Where(claim => RoleClaimTypes.Contains(claim.Type, StringComparer.Ordinal))
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool HasRole(ClaimsPrincipal? user, string role)
    {
        if (user == null || string.IsNullOrWhiteSpace(role))
        {
            return false;
        }

        return user.Claims.Any(claim =>
            RoleClaimTypes.Contains(claim.Type, StringComparer.Ordinal) &&
            string.Equals(claim.Value, role, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns a display name for the signed-in administrator, falling back to the
    /// subject identifier. Returns null when the token carries neither.
    /// </summary>
    public static string? GetDisplayName(ClaimsPrincipal? user)
    {
        if (user == null)
        {
            return null;
        }

        return FindFirst(user, NameClaimTypes) ?? FindFirst(user, SubjectClaimTypes);
    }

    private static string? FindFirst(ClaimsPrincipal user, string[] claimTypes)
    {
        foreach (var claimType in claimTypes)
        {
            var value = user.FindFirst(claimType)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
