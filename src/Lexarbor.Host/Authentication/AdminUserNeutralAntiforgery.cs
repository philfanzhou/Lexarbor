using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;

namespace Lexarbor.Host.Authentication;

/// <summary>
/// The user-neutral antiforgery boundary of the hosted login. The framework's own
/// <c>IAntiforgery</c> binds every request token to the principal of the request that minted
/// it, so a pair fetched before the sign-in would stop validating once the same browser holds
/// a session. The replaced SignaCore client's boundary was deliberately user-neutral — "the
/// pair is bound to the per-browser cookie binding alone, never against the principal any
/// pipeline stage presents" — and the SPA relies on exactly that: one fetch is reused until a
/// 401 retires it, across the login. This wrapper evaluates the framework implementation
/// against an anonymous principal on both legs, keeping every cryptographic check (the cookie
/// binding included) while removing only the principal-binding dimension.
/// </summary>
public sealed class AdminUserNeutralAntiforgery(IAntiforgery framework) : IAntiforgery
{
    public AntiforgeryTokenSet GetAndStoreTokens(HttpContext context) =>
        AsAnonymous(context, () => framework.GetAndStoreTokens(context));

    public AntiforgeryTokenSet GetTokens(HttpContext context) =>
        AsAnonymous(context, () => framework.GetTokens(context));

    public async Task<bool> IsRequestValidAsync(HttpContext context)
    {
        var user = context.User;
        context.User = new ClaimsPrincipal(new ClaimsIdentity());
        try
        {
            return await framework.IsRequestValidAsync(context);
        }
        finally
        {
            context.User = user;
        }
    }

    public async Task ValidateRequestAsync(HttpContext context)
    {
        var user = context.User;
        context.User = new ClaimsPrincipal(new ClaimsIdentity());
        try
        {
            await framework.ValidateRequestAsync(context);
        }
        finally
        {
            context.User = user;
        }
    }

    public void SetCookieTokenAndHeader(HttpContext context)
    {
        var user = context.User;
        context.User = new ClaimsPrincipal(new ClaimsIdentity());
        try
        {
            framework.SetCookieTokenAndHeader(context);
        }
        finally
        {
            context.User = user;
        }
    }

    private static T AsAnonymous<T>(HttpContext context, Func<T> call)
    {
        var user = context.User;
        context.User = new ClaimsPrincipal(new ClaimsIdentity());
        try
        {
            return call();
        }
        finally
        {
            context.User = user;
        }
    }
}
