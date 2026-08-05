using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Ruoyu.Study.Vocabulary.Host.Authentication;

public sealed class AdminRoleRequirement : IAuthorizationRequirement;

/// <summary>
/// Grants the <c>VocabularyAdmin</c> policy when the validated principal carries the
/// configured administrator role.
///
/// A requirement handler rather than <c>RequireRole</c> or an inline assertion, for two
/// reasons: role claims arrive under either the short or the URI claim type depending on
/// the issuer (see <see cref="VocabularyClaims"/>), and the required role name is read
/// from options at evaluation time so it stays configurable.
/// </summary>
public sealed class AdminRoleHandler : AuthorizationHandler<AdminRoleRequirement>
{
    private readonly IOptionsMonitor<AdminAuthenticationOptions> _options;

    public AdminRoleHandler(IOptionsMonitor<AdminAuthenticationOptions> options)
    {
        _options = options;
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AdminRoleRequirement requirement)
    {
        if (VocabularyClaims.HasRole(context.User, _options.CurrentValue.RequiredRole))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
