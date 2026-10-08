using Microsoft.Extensions.Options;
using SignaCore.Client.AspNetCore;

namespace Lexarbor.Host.Authentication;

/// <summary>
/// The pre-sign-in administrator gate (#209's original extension point): the package calls
/// it after strictly validating and correlating both tokens but before any ticket or cookie
/// is written, with the verified access-token principal as input. Only a subject holding
/// the configured administrator role may establish a session; every other outcome —
/// denial, timeout, failure — the package reports as the closed
/// <see cref="SignaCoreSignInReason.PreSignInDenied"/> reason, which the response writer
/// maps to the repository's <c>denied</c> vocabulary.
/// </summary>
public sealed class AdminPreSignInRoleGate(IOptionsMonitor<AdminAuthenticationOptions> options)
    : ISignaCorePreSignInAuthorizationDecision
{
    public ValueTask<SignaCoreAuthorizationDecisionResult> DecideAsync(
        SignaCorePreSignInAuthorizationContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            VocabularyClaims.HasRole(context.AccessTokenPrincipal, options.CurrentValue.RequiredRole)
                ? SignaCoreAuthorizationDecisionResult.Allowed
                : SignaCoreAuthorizationDecisionResult.Denied);
    }
}
