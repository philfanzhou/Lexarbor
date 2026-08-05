using System.Security.Claims;

namespace Lexarbor.Host.Authentication;

/// <summary>
/// Reads identity information from a validated <see cref="ClaimsPrincipal"/>.
///
/// OIDC issuers normally emit short claim names ("sub", "name", "role"). Some .NET
/// issuers emit full <see cref="ClaimTypes"/> URIs, so both shapes are accepted.
/// </summary>
public static class VocabularyClaims
{
    private static readonly string[] RoleClaimTypes = ["role", ClaimTypes.Role];

    private static readonly string[] NameClaimTypes =
    [
        "name",
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
