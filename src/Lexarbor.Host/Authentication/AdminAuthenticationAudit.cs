using System.Security.Claims;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Repositories;
using Microsoft.Extensions.Options;
using ServiceMantle.Audit;
using ServiceMantle.Management;

namespace Lexarbor.Host.Authentication;

/// <summary>
/// Records the administrator authentication boundary events — hosted sign-in, failed sign-in
/// and logout — into the ServiceMantle management audit log (<c>service_audit_logs</c>).
/// </summary>
/// <remarks>
/// <para>
/// Every value that crosses the provider or browser boundary is kept out of the audit rows by
/// construction: codes, states, tokens, client secrets, session handles and provider error
/// descriptions never enter an event, and the fixed failure reasons are the only failure
/// classification. The ServiceMantle sensitive-content policy in
/// <see cref="ManagementAuditEvent.Create"/> is the enforced boundary for everything that does
/// enter.
/// </para>
/// <para>
/// The sign-in and logout rows are staged through the caller's session transaction and commit
/// or roll back with it; the failed-login row has no session write to join and is saved
/// standalone under the session storage failure semantics, so an audit save failure never
/// silently loses a security event.
/// </para>
/// </remarks>
public sealed class AdminAuthenticationAudit(
    IManagementAuditWriter auditWriter,
    IManagementCurrentOperatorResolver operatorResolver,
    AdminSessionRepository sessionRepository,
    IUnitOfWork unitOfWork,
    IOptionsMonitor<AdminAuthenticationOptions> options,
    TimeProvider clock)
{
    /// <summary>The metadata key carrying the fixed failure reason of a failed login.</summary>
    public const string FailureReasonMetadataKey = "reason";

    private const string LoginSucceededDescription = "Administrator signed in through the hosted login.";
    private const string LoginFailedDescription = "Administrator hosted login failed.";
    private const string LogoutDescription = "Administrator signed out; the session was revoked.";
    private const string UnknownTargetId = "unknown";

    /// <summary>
    /// Builds the participating write that stages the <c>admin_login.succeeded</c> row for a
    /// verified callback principal. The event (including the management identity projection of
    /// the callback principal) is validated here, before any session write: a principal whose
    /// subject cannot be represented in the audit operator model throws and fails the sign-in
    /// closed instead of being truncated or rewritten.
    /// </summary>
    public Func<Task> StageLoginSucceeded(HttpContext context, ClaimsPrincipal validatedPrincipal)
    {
        // The callback principal is minted a ServiceMantle management identity exactly like the
        // session and Bearer paths do; the resolver is then the single validation authority.
        ManagementIdentityMapping.Apply(
            validatedPrincipal,
            options.CurrentValue.RequiredRole,
            WellKnownManagementAuditOperatorSources.InteractiveAdmin.Value);
        var resolved = operatorResolver.Resolve(validatedPrincipal);
        var auditOperator = resolved.Operator;
        if (resolved.Status != ManagementCurrentOperatorStatus.Resolved
            || auditOperator is null || auditOperator.OperatorId is null)
        {
            throw new ManagementAuditException(
                "audit.operator_id_invalid",
                "The verified administrator principal cannot be projected onto the management audit operator.");
        }

        var auditEvent = CreateEvent(
            auditOperator,
            WellKnownManagementAuditActions.AdminLoginSucceeded,
            auditOperator.OperatorId,
            ManagementAuditOutcome.Success,
            context,
            LoginSucceededDescription);
        return () => StageAsync(auditEvent, context.RequestAborted);
    }

    /// <summary>
    /// Persists exactly one <c>admin_login.failed</c> row for a failed login callback. The
    /// operator is anonymous — a failed login established no identity — and the only failure
    /// classification is the fixed reason the browser is redirected with.
    /// </summary>
    public Task RecordLoginFailedAsync(HttpContext context, string reason, CancellationToken cancellationToken)
    {
        var auditEvent = ManagementAuditEvent.Create(
            ManagementAuditOperator.Create(WellKnownManagementAuditOperatorSources.Anonymous),
            WellKnownManagementAuditActions.AdminLoginFailed,
            ManagementAuditTarget.Create(WellKnownManagementAuditTargetTypes.AdminSession, UnknownTargetId),
            OutcomeForReason(reason),
            timeProvider: clock,
            clientIp: context.Connection.RemoteIpAddress?.ToString(),
            correlationId: context.GetServiceMantleCorrelationId(),
            securityDescription: LoginFailedDescription,
            metadata: new Dictionary<string, string> { [FailureReasonMetadataKey] = reason });
        return sessionRepository.ExecuteStandaloneWriteAsync(
            () => StageAsync(auditEvent, cancellationToken));
    }

    /// <summary>
    /// Builds the hook that stages the <c>admin_login.logout</c> row when — and only when — a
    /// live session is revoked. The operator comes from the revoked snapshot's own verified
    /// subject and display name.
    /// </summary>
    public Func<ValidatedAdminSession, Task> StageLogout(HttpContext context) => session =>
    {
        ManagementAuditEvent auditEvent;
        try
        {
            auditEvent = ManagementAuditEvent.Create(
                ManagementAuditOperator.Create(
                    WellKnownManagementAuditOperatorSources.InteractiveAdmin,
                    session.Subject,
                    session.DisplayName),
                WellKnownManagementAuditActions.AdminLogout,
                ManagementAuditTarget.Create(WellKnownManagementAuditTargetTypes.AdminSession, session.Subject),
                ManagementAuditOutcome.Success,
                timeProvider: clock,
                clientIp: context.Connection.RemoteIpAddress?.ToString(),
                correlationId: context.GetServiceMantleCorrelationId(),
                securityDescription: LogoutDescription);
        }
        catch (ManagementAuditException)
        {
            // A snapshot whose subject cannot be represented in the audit operator model cannot
            // be revoked silently either: the revocation rolls back and the caller sees the
            // fixed session storage failure instead of a logout that lost its audit row.
            throw new AdminSessionStorageException();
        }

        return StageAsync(auditEvent, context.RequestAborted);
    };

    private async Task StageAsync(ManagementAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        // Inside the caller's session transaction SaveChangesAsync is a re-entrant pass-through
        // on the same scoped context, so the audit row joins that transaction; standalone, the
        // shared write lock serializes the save.
        await auditWriter.RecordAsync(auditEvent, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private ManagementAuditEvent CreateEvent(
        ManagementAuditOperator auditOperator,
        ManagementAuditAction action,
        string targetId,
        ManagementAuditOutcome outcome,
        HttpContext context,
        string securityDescription) =>
        ManagementAuditEvent.Create(
            auditOperator,
            action,
            ManagementAuditTarget.Create(WellKnownManagementAuditTargetTypes.AdminSession, targetId),
            outcome,
            timeProvider: clock,
            clientIp: context.Connection.RemoteIpAddress?.ToString(),
            correlationId: context.GetServiceMantleCorrelationId(),
            securityDescription: securityDescription);

    private static ManagementAuditOutcome OutcomeForReason(string reason) => reason switch
    {
        // The two refusals — the provider denied the administrator, and the administrator
        // canceled at the provider's page — are policy refusals, not failed attempts.
        "denied" or "canceled" => ManagementAuditOutcome.Denied,
        _ => ManagementAuditOutcome.Failure
    };
}
