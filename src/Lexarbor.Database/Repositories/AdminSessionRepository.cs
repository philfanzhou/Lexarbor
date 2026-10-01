using Lexarbor.Database.Entities;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

/// <summary>Only opaque ciphertext and metadata cross this persistence boundary.</summary>
public sealed class AdminSessionRepository(VocabularyDbContext context, IUnitOfWork unitOfWork)
{
    private static readonly AsyncLocal<bool> SessionOperation = new();
    public static bool IsSessionOperation => SessionOperation.Value;
    public const int CleanupBatchSize = 100;
    public const string StorageFailureMessage = "Administrator session storage is unavailable.";

    public Task<AdminSessionEntity?> ReadAsync(string hash, CancellationToken cancellationToken = default) =>
        SafelyAsync(() => context.AdminSessions.AsNoTracking().SingleOrDefaultAsync(
            row => row.HandleHash == hash, cancellationToken));

    public Task CreateOrReplaceAsync(AdminSessionEntity row, string? oldHash = null,
        CancellationToken cancellationToken = default) => CreateOrReplaceAsync(row, oldHash, null, cancellationToken);

    /// <summary>
    /// Creates or replaces a session row, running <paramref name="participatingWrite"/> inside
    /// the same serialized transaction so a companion write (the management audit row for a
    /// sign-in) commits with the session or rolls back with it. The delegate must save its own
    /// changes through the unit of work; it must not start another session write.
    /// </summary>
    public Task CreateOrReplaceAsync(AdminSessionEntity row, string? oldHash, Func<Task>? participatingWrite,
        CancellationToken cancellationToken = default) => SafelyAsync(() => WriteAsync(async () =>
    {
        if (participatingWrite is not null) await participatingWrite();
        // Parameterized bulk SQL avoids tracked snapshots and pending entities surviving rollback.
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO admin_session (handle_hash, expires_at_unix_ms, protected_payload) VALUES ({row.HandleHash}, {row.ExpiresAtUnixMs}, {row.ProtectedPayload})", cancellationToken);
        if (oldHash is not null)
            await context.AdminSessions.Where(item => item.HandleHash == oldHash).ExecuteDeleteAsync(cancellationToken);
        return 0;
    }, cancellationToken));

    public Task<AdminSessionEntity?> RevokeAndReadAsync(string hash, CancellationToken cancellationToken = default) =>
        RevokeAndReadAsync(hash, null, cancellationToken);

    /// <summary>
    /// Revokes a session row, running <paramref name="participatingWrite"/> with the selected row
    /// inside the same serialized transaction so a companion write keyed on the revoked session
    /// (the management audit row for a logout) commits with the revocation or rolls back with it.
    /// The delegate receives the selected row (null when nothing matched) and must save its own
    /// changes through the unit of work.
    /// </summary>
    public Task<AdminSessionEntity?> RevokeAndReadAsync(string hash, Func<AdminSessionEntity?, Task>? participatingWrite,
        CancellationToken cancellationToken = default) =>
        SafelyAsync(() => WriteAsync(async () =>
        {
            var row = await context.AdminSessions.AsNoTracking().SingleOrDefaultAsync(
                item => item.HandleHash == hash, cancellationToken);
            if (participatingWrite is not null) await participatingWrite(row);
            await context.AdminSessions.Where(item => item.HandleHash == hash).ExecuteDeleteAsync(cancellationToken);
            return row;
        }, cancellationToken));

    /// <summary>
    /// Runs a write that is not a session write but must follow the administrator session
    /// storage failure semantics: its storage errors are translated into the fixed failure
    /// family the session middleware answers, and its database activity is logged under the
    /// same EF Core suppression. Used for the standalone management audit row a failed login
    /// leaves behind — a path with no session transaction to join. The delegate performs its
    /// own save through the unit of work (the shared write lock serializes it).
    /// </summary>
    public Task ExecuteStandaloneWriteAsync(Func<Task> write) => SafelyAsync(async () =>
    {
        await write();
        return 0;
    });

    public Task<int> CleanupAsync(long nowUnixMs, CancellationToken cancellationToken = default) =>
        SafelyAsync(() => WriteAsync(() => context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM admin_session WHERE handle_hash IN (SELECT handle_hash FROM admin_session WHERE expires_at_unix_ms <= {nowUnixMs} ORDER BY expires_at_unix_ms, handle_hash LIMIT {CleanupBatchSize})",
            cancellationToken), cancellationToken));

    private Task<T> WriteAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        // Joining an outer transaction could return a handle/snapshot before its commit.
        // Refuse nested session writes (including another scoped context in that flow).
        if (UnitOfWork.IsWriteInProgress || context.Database.CurrentTransaction is not null)
            throw new AdminSessionStorageException();
        return unitOfWork.ExecuteInTransactionAsync(action, cancellationToken);
    }

    private static async Task<T> SafelyAsync<T>(Func<Task<T>> operation)
    {
        var previous = SessionOperation.Value;
        SessionOperation.Value = true;
        try { return await operation(); }
        catch (OperationCanceledException) { throw; }
        catch (StorageBusyException) { throw new StorageBusyException(StorageFailureMessage); }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        { throw new StorageBusyException(StorageFailureMessage); }
        catch (Exception exception) when (exception is SqliteException or DbUpdateException or ConflictException or InvalidOperationException)
        { throw new AdminSessionStorageException(); }
        finally { SessionOperation.Value = previous; }
    }
}

public sealed class AdminSessionStorageException() : Exception(AdminSessionRepository.StorageFailureMessage);
