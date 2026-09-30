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
        CancellationToken cancellationToken = default) => SafelyAsync(() => WriteAsync(async () =>
    {
        // Parameterized bulk SQL avoids tracked snapshots and pending entities surviving rollback.
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO admin_session (handle_hash, expires_at_unix_ms, protected_payload) VALUES ({row.HandleHash}, {row.ExpiresAtUnixMs}, {row.ProtectedPayload})", cancellationToken);
        if (oldHash is not null)
            await context.AdminSessions.Where(item => item.HandleHash == oldHash).ExecuteDeleteAsync(cancellationToken);
        return 0;
    }, cancellationToken));

    public Task<AdminSessionEntity?> RevokeAndReadAsync(string hash, CancellationToken cancellationToken = default) =>
        SafelyAsync(() => WriteAsync(async () =>
        {
            var row = await context.AdminSessions.AsNoTracking().SingleOrDefaultAsync(
                item => item.HandleHash == hash, cancellationToken);
            await context.AdminSessions.Where(item => item.HandleHash == hash).ExecuteDeleteAsync(cancellationToken);
            return row;
        }, cancellationToken));

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
