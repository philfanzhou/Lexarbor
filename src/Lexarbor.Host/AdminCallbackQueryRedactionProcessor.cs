using System.Diagnostics;
using OpenTelemetry;

namespace Lexarbor.Host;

/// <summary>
/// Redacts correlation query material from spans of the hosted-login callback
/// and the prepared-logout return route. Those URLs necessarily carry a
/// one-time authorization code or state; no span attribute may export them.
/// </summary>
internal sealed class AdminCallbackQueryRedactionProcessor : BaseProcessor<Activity>
{
    internal const string RedactedValue = "[REDACTED]";

    private static readonly string[] QueryBearingAttributes =
    [
        "url.query",
        "url.full",
        "http.url",
        "http.target"
    ];

    public override void OnEnd(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        var path = activity.GetTagItem("url.path") as string
            ?? activity.GetTagItem("http.route") as string;
        // Routing accepts one optional trailing slash; trim it before matching.
        var normalized = path?.TrimEnd('/');
        if (normalized is not ("/admin/auth/callback" or "/admin/auth/logout/return"))
        {
            return;
        }

        foreach (var name in QueryBearingAttributes)
        {
            if (activity.GetTagItem(name) is not null)
            {
                activity.SetTag(name, RedactedValue);
            }
        }
    }
}
