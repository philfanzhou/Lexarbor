using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Lexarbor.Database.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Lexarbor.Database.LegacySqlite;

/// <summary>Where the transition release looks for the SQLite database of an earlier release.</summary>
/// <param name="SourcePath">An absolute file path, or null when no source is configured.</param>
public sealed record LegacySqliteImportOptions(string? SourcePath);

/// <summary>A refused or failed import. The message is fixed text: never a path, a connection string, SQL, or row data.</summary>
public sealed class LegacySqliteImportException(string message) : InvalidOperationException(message);

/// <summary>
/// Copies the SQLite database of an earlier release into PostgreSQL once, as
/// ADR-011's transition release decides, and records the outcome in
/// <c>legacy_sqlite_import</c>.
/// </summary>
/// <remarks>
/// <para>
/// It runs inside <see cref="LexarborMigrationExecutor"/>, so the ServiceMantle
/// migration lock already makes it the only copier, and only after the
/// PostgreSQL schema is current. The marker row decides everything:
/// </para>
/// <list type="bullet">
/// <item>A marker exists: nothing to do. A database initialized without a
/// source (<c>none</c>) refuses a SQLite file that appears later, because
/// copying it would merge two histories.</item>
/// <item>No marker and no source: a new installation; the <c>none</c> marker is
/// written.</item>
/// <item>No marker and a source: the copy below.</item>
/// </list>
/// <para>
/// The source file is never opened for writing. It is backed up to a private
/// working copy next to it, the bundled SQLite migrations bring that copy to
/// the last SQLite schema, and the copy is read. In one PostgreSQL
/// transaction every table is checked empty, copied in foreign-key order with
/// user triggers disabled, identity sequences are advanced, every table is
/// verified, and the <c>imported</c> marker is written. Any failure rolls the
/// whole transaction back, so PostgreSQL holds no copied row and no marker,
/// and the next start tries again.
/// </para>
/// <para>
/// Verification compares, per table, the row count and an order-independent
/// sum of per-row SHA-256 digests over every copied column and every generated
/// column, computed from the converted source values and from what PostgreSQL
/// returns. Including the generated columns proves that PostgreSQL's
/// normalization reproduces SQLite's. Timestamps are compared at
/// PostgreSQL's microsecond precision, to which the copy truncates them.
/// </para>
/// </remarks>
public sealed class LegacySqliteImporter(
    VocabularyDbContext context,
    LegacySqliteImportOptions options,
    ILogger<LegacySqliteImporter> logger,
    TimeProvider? timeProvider = null)
{
    internal const string MarkerTable = "legacy_sqlite_import";
    internal const string InsertionOrderColumn = "insertion_order";

    internal const string TargetNotEmpty =
        "Legacy SQLite import refused: the PostgreSQL database already contains data. "
        + "Start with an empty PostgreSQL database to import the SQLite database, or move the SQLite file away to keep the PostgreSQL data.";

    internal const string SourceAfterInitialization =
        "Legacy SQLite import refused: this PostgreSQL database was initialized without a SQLite database, but one is now present. "
        + "Move the SQLite file away, or start with an empty PostgreSQL database to import it.";

    internal const string SourceUnreadable =
        "Legacy SQLite import failed: the SQLite database could not be read. "
        + "If the previous release was stopped abruptly, start it once so that it recovers the database, then upgrade again.";

    internal const string SourceTooNew =
        "Legacy SQLite import refused: the SQLite database has a schema newer than this release.";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>Test seam: runs after the copy and before verification, inside the import transaction.</summary>
    internal Func<NpgsqlConnection, CancellationToken, Task>? BeforeVerification { get; init; }

    private bool SourceExists => options.SourcePath is { } path && File.Exists(path);

    /// <summary>
    /// Whether the migration executor still has work here: no marker yet, or a
    /// source next to a database initialized without one (which
    /// <see cref="RunAsync"/> then refuses). Read-only; requires the current
    /// schema.
    /// </summary>
    public async Task<bool> RequiresExecutionAsync(CancellationToken cancellationToken)
    {
        var marker = await ReadMarkerAsync(cancellationToken);
        return marker is null || (marker.Outcome == LegacySqliteImportEntity.None && SourceExists);
    }

    /// <summary>Decides and, when needed, performs the import. Requires the current schema.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var marker = await ReadMarkerAsync(cancellationToken);
        if (marker is not null)
        {
            if (marker.Outcome == LegacySqliteImportEntity.None && SourceExists)
            {
                logger.LogError("{Diagnostic}", SourceAfterInitialization);
                throw Refuse(SourceAfterInitialization);
            }

            return;
        }

        if (!SourceExists)
        {
            context.LegacySqliteImports.Add(new LegacySqliteImportEntity
            {
                Outcome = LegacySqliteImportEntity.None,
                CompletedAtUnixMs = _clock.GetUtcNow().ToUnixTimeMilliseconds(),
            });
            await context.SaveChangesAsync(cancellationToken);
            context.ChangeTracker.Clear();
            logger.LogInformation("No SQLite database of an earlier release was found; PostgreSQL starts without an import.");
            return;
        }

        await ImportAsync(options.SourcePath!, cancellationToken);
    }

    private Task<LegacySqliteImportEntity?> ReadMarkerAsync(CancellationToken cancellationToken) =>
        context.LegacySqliteImports.AsNoTracking().SingleOrDefaultAsync(cancellationToken);

    private async Task ImportAsync(string source, CancellationToken cancellationToken)
    {
        logger.LogInformation("Copying the SQLite database of an earlier release into PostgreSQL; the service is not ready until the copy is verified.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(source))!;
        var workingCopy = Path.Combine(directory, $".lexarbor-import-{Guid.NewGuid():N}.db");
        try
        {
            CreateWorkingCopy(source, workingCopy);
            var version = await MigrateWorkingCopyAsync(workingCopy, cancellationToken);
            var (tables, rows) = await CopyAsync(workingCopy, version, cancellationToken);
            logger.LogInformation(
                "SQLite import finished: {Tables} tables and {Rows} rows copied and verified. The SQLite file was left unchanged.",
                tables, rows);
        }
        catch (LegacySqliteImportException exception)
        {
            logger.LogError("{Diagnostic}", exception.Message);
            throw;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            // The working copy and whatever sidecars SQLite created for it
            // (rollback journal, or -wal and -shm when a migration switched
            // the journal mode) share its unique name.
            foreach (var file in Directory.EnumerateFiles(directory, Path.GetFileName(workingCopy) + "*"))
            {
                TryDelete(file);
            }
        }
    }

    /// <summary>
    /// Backs the source up through SQLite, which reads committed frames of a
    /// leftover <c>-wal</c> file too. Reading a database in WAL mode creates
    /// <c>-wal</c> and <c>-shm</c> files that a read-only connection cannot
    /// remove; the ones this read created are removed afterwards, and ones that
    /// were already there are left as found.
    /// </summary>
    private void CreateWorkingCopy(string source, string workingCopy)
    {
        var sidecars = new[] { source + "-wal", source + "-shm" }.Where(path => !File.Exists(path)).ToList();
        try
        {
            using var from = new SqliteConnection(Sqlite(source, SqliteOpenMode.ReadOnly));
            from.Open();
            using var to = new SqliteConnection(Sqlite(workingCopy, SqliteOpenMode.ReadWriteCreate));
            to.Open();
            from.BackupDatabase(to);
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            throw new LegacySqliteImportException(SourceUnreadable);
        }
        finally
        {
            foreach (var created in sidecars.Where(File.Exists))
            {
                TryDelete(created);
            }
        }
    }

    private static async Task<string> MigrateWorkingCopyAsync(string workingCopy, CancellationToken cancellationToken)
    {
        try
        {
            await using var legacy = LegacySqliteMigrationContext.Open(Sqlite(workingCopy, SqliteOpenMode.ReadWrite));
            var known = legacy.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
            var applied = await legacy.Database.GetAppliedMigrationsAsync(cancellationToken);
            if (applied.Any(id => !known.Contains(id)))
            {
                throw new LegacySqliteImportException(SourceTooNew);
            }

            await legacy.Database.MigrateAsync(cancellationToken);
            return LegacySqliteMigrationContext.LastMigration;
        }
        catch (Exception exception) when (exception is SqliteException or DbUpdateException or InvalidOperationException
            and not LegacySqliteImportException)
        {
            throw new LegacySqliteImportException(
                "Legacy SQLite import failed: the SQLite database could not be brought to the last SQLite schema.");
        }
    }

    private async Task<(int Tables, long Rows)> CopyAsync(string workingCopy, string version, CancellationToken cancellationToken)
    {
        var plan = PlanTables(context.Model);
        var stage = "opening the databases";
        try
        {
            await using var sqlite = new SqliteConnection(Sqlite(workingCopy, SqliteOpenMode.ReadOnly));
            await sqlite.OpenAsync(cancellationToken);
            await using var postgres = new NpgsqlConnection(context.Database.GetConnectionString());
            await postgres.OpenAsync(cancellationToken);
            await using var transaction = await postgres.BeginTransactionAsync(cancellationToken);

            stage = "comparing the table sets";
            await using (var tablesCommand = sqlite.CreateCommand())
            {
                tablesCommand.CommandText =
                    "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\' "
                    + "AND name NOT IN ('__EFMigrationsHistory', '__EFMigrationsLock') ORDER BY name";
                await using var reader = await tablesCommand.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var name = reader.GetString(0);
                    if (plan.All(table => table.Name != name))
                    {
                        // A table PostgreSQL does not have would be lost.
                        throw new LegacySqliteImportException(
                            $"Legacy SQLite import failed: the SQLite database has table {name}, which PostgreSQL does not have.");
                    }
                }
            }

            stage = "checking that PostgreSQL is empty";
            foreach (var table in plan)
            {
                if (await ScalarAsync<bool>(postgres, $"SELECT EXISTS (SELECT 1 FROM {Quote(table.Name)})", cancellationToken))
                {
                    throw new LegacySqliteImportException(TargetNotEmpty);
                }
            }

            stage = "disabling triggers";
            foreach (var table in plan)
            {
                await ExecuteAsync(postgres, $"ALTER TABLE {Quote(table.Name)} DISABLE TRIGGER USER", cancellationToken);
            }

            var expected = new Dictionary<string, TableDigest>(StringComparer.Ordinal);
            long rows = 0;
            foreach (var table in plan)
            {
                stage = $"copying table {table.Name}";
                var columns = MatchSourceColumns(sqlite, table);
                expected[table.Name] = await CopyTableAsync(sqlite, postgres, table, columns, cancellationToken);
                rows += expected[table.Name].Count;
            }

            stage = "advancing identity sequences";
            await AdvanceIdentitySequencesAsync(postgres, cancellationToken);

            if (BeforeVerification is { } hook)
            {
                await hook(postgres, cancellationToken);
            }

            foreach (var table in plan)
            {
                stage = $"verifying table {table.Name}";
                var actual = await DigestTargetAsync(postgres, table, cancellationToken);
                if (actual != expected[table.Name])
                {
                    throw new LegacySqliteImportException(
                        $"Legacy SQLite import failed: verification found a difference in table {table.Name}. Nothing was committed.");
                }
            }

            stage = "enabling triggers";
            foreach (var table in plan)
            {
                await ExecuteAsync(postgres, $"ALTER TABLE {Quote(table.Name)} ENABLE TRIGGER USER", cancellationToken);
            }

            stage = "recording the import";
            await using (var marker = postgres.CreateCommand())
            {
                marker.CommandText =
                    $"INSERT INTO {Quote(MarkerTable)} (id, outcome, source_schema_version, copied_tables, copied_rows, completed_at_unix_ms) "
                    + "VALUES (@id, @outcome, @version, @tables, @rows, @now)";
                marker.Parameters.AddWithValue("id", LegacySqliteImportEntity.SingletonId);
                marker.Parameters.AddWithValue("outcome", LegacySqliteImportEntity.Imported);
                marker.Parameters.AddWithValue("version", version);
                marker.Parameters.AddWithValue("tables", plan.Count);
                marker.Parameters.AddWithValue("rows", rows);
                marker.Parameters.AddWithValue("now", _clock.GetUtcNow().ToUnixTimeMilliseconds());
                await marker.ExecuteNonQueryAsync(cancellationToken);
            }

            stage = "committing";
            await transaction.CommitAsync(cancellationToken);
            return (plan.Count, rows);
        }
        catch (Exception exception) when (exception is not (LegacySqliteImportException or OperationCanceledException))
        {
            // Provider messages can carry SQL, key values or connection detail;
            // only the stage reaches the diagnostic.
            throw new LegacySqliteImportException($"Legacy SQLite import failed while {stage}. Nothing was committed.");
        }
    }

    internal sealed record ColumnPlan(string Name, Type ProviderType, NpgsqlDbType DbType, bool Computed);

    internal sealed record TablePlan(string Name, IReadOnlyList<ColumnPlan> Columns);

    private readonly record struct TableDigest(long Count, UInt128 Sum);

    /// <summary>Every mapped table except the marker, principal tables before their dependents.</summary>
    internal static IReadOnlyList<TablePlan> PlanTables(IModel model)
    {
        var tables = new SortedDictionary<string, TablePlan>(StringComparer.Ordinal);
        var principals = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var entityType in model.GetEntityTypes())
        {
            var name = entityType.GetTableName();
            if (name is null || name == MarkerTable)
            {
                continue;
            }

            if (tables.ContainsKey(name))
            {
                throw new InvalidOperationException($"Table {name} is mapped by more than one entity type.");
            }

            var store = StoreObjectIdentifier.Table(name, entityType.GetSchema());
            var columns = entityType.GetProperties()
                .Select(property => new ColumnPlan(
                    property.GetColumnName(store)!,
                    Nullable.GetUnderlyingType(property.GetProviderClrType() ?? property.ClrType)
                        ?? property.GetProviderClrType() ?? property.ClrType,
                    DbTypeFor(property.GetColumnType()),
                    property.GetComputedColumnSql() is not null))
                .OrderBy(column => column.Name, StringComparer.Ordinal)
                .ToList();
            tables[name] = new TablePlan(name, columns);
            principals[name] = entityType.GetForeignKeys()
                .Select(foreignKey => foreignKey.PrincipalEntityType.GetTableName()!)
                .Where(principal => principal != name)
                .ToHashSet(StringComparer.Ordinal);
        }

        var ordered = new List<TablePlan>();
        var placed = new HashSet<string>(StringComparer.Ordinal);
        while (ordered.Count < tables.Count)
        {
            var next = tables.Keys.FirstOrDefault(name => !placed.Contains(name) && principals[name].All(placed.Contains))
                ?? throw new InvalidOperationException("The foreign keys between the mapped tables form a cycle.");
            placed.Add(next);
            ordered.Add(tables[next]);
        }

        return ordered;
    }

    private static NpgsqlDbType DbTypeFor(string columnType) => columnType switch
    {
        "text" => NpgsqlDbType.Text,
        "bigint" => NpgsqlDbType.Bigint,
        "integer" => NpgsqlDbType.Integer,
        "smallint" => NpgsqlDbType.Smallint,
        "boolean" => NpgsqlDbType.Boolean,
        "timestamp with time zone" => NpgsqlDbType.TimestampTz,
        _ when columnType.StartsWith("character varying", StringComparison.Ordinal) => NpgsqlDbType.Varchar,
        _ => throw new InvalidOperationException($"Column type {columnType} has no legacy SQLite import mapping."),
    };

    /// <summary>
    /// The SQLite expression for each planned column, in plan order. A copied
    /// column must be an ordinary SQLite column; the insertion order, which
    /// SQLite kept in the implicit rowid, comes from it; a generated column
    /// must be generated in SQLite too. A SQLite column the plan does not
    /// cover would be lost, so it fails the import.
    /// </summary>
    private static IReadOnlyList<string> MatchSourceColumns(SqliteConnection sqlite, TablePlan table)
    {
        var source = new Dictionary<string, bool>(StringComparer.Ordinal);
        using (var info = sqlite.CreateCommand())
        {
            info.CommandText = $"SELECT name, hidden FROM pragma_table_xinfo({Literal(table.Name)})";
            using var reader = info.ExecuteReader();
            while (reader.Read())
            {
                source[reader.GetString(0)] = reader.GetInt64(1) is 2 or 3;
            }
        }

        LegacySqliteImportException Mismatch() =>
            new($"Legacy SQLite import failed: the SQLite schema of table {table.Name} does not match PostgreSQL.");

        if (source.Count == 0)
        {
            throw Mismatch();
        }

        var expressions = new List<string>(table.Columns.Count);
        foreach (var column in table.Columns)
        {
            if (source.TryGetValue(column.Name, out var generated) && generated == column.Computed)
            {
                expressions.Add(Quote(column.Name));
            }
            else if (!source.ContainsKey(column.Name) && column.Name == InsertionOrderColumn && !column.Computed)
            {
                expressions.Add("rowid");
            }
            else
            {
                throw Mismatch();
            }
        }

        if (source.Keys.Any(name => table.Columns.All(column => column.Name != name)))
        {
            throw Mismatch();
        }

        return expressions;
    }

    private static async Task<TableDigest> CopyTableAsync(
        SqliteConnection sqlite, NpgsqlConnection postgres, TablePlan table, IReadOnlyList<string> sourceColumns,
        CancellationToken cancellationToken)
    {
        var copied = table.Columns.Where(column => !column.Computed).ToList();
        await using var select = sqlite.CreateCommand();
        select.CommandText = $"SELECT {string.Join(", ", sourceColumns)} FROM {Quote(table.Name)}";
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        await using var writer = await postgres.BeginBinaryImportAsync(
            $"COPY {Quote(table.Name)} ({string.Join(", ", copied.Select(column => Quote(column.Name)))}) FROM STDIN (FORMAT BINARY)",
            cancellationToken);
        writer.Timeout = TimeSpan.Zero;

        var digest = new RowDigest();
        var values = new object?[table.Columns.Count];
        long count = 0;
        UInt128 sum = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            await writer.StartRowAsync(cancellationToken);
            for (var i = 0; i < table.Columns.Count; i++)
            {
                var column = table.Columns[i];
                values[i] = ReadSqlite(reader, i, column.ProviderType);
                if (column.Computed)
                {
                    continue;
                }

                if (values[i] is null)
                {
                    await writer.WriteNullAsync(cancellationToken);
                }
                else
                {
                    await writer.WriteAsync(values[i], column.DbType, cancellationToken);
                }
            }

            sum += digest.Of(values);
            count++;
        }

        await writer.CompleteAsync(cancellationToken);
        return new TableDigest(count, sum);
    }

    private static async Task<TableDigest> DigestTargetAsync(
        NpgsqlConnection postgres, TablePlan table, CancellationToken cancellationToken)
    {
        await using var select = postgres.CreateCommand();
        select.CommandTimeout = 0;
        select.CommandText = $"SELECT {string.Join(", ", table.Columns.Select(column => Quote(column.Name)))} FROM {Quote(table.Name)}";
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        var digest = new RowDigest();
        var values = new object?[table.Columns.Count];
        long count = 0;
        UInt128 sum = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            for (var i = 0; i < table.Columns.Count; i++)
            {
                values[i] = ReadPostgres(reader, i, table.Columns[i].ProviderType);
            }

            sum += digest.Of(values);
            count++;
        }

        return new TableDigest(count, sum);
    }

    private static async Task AdvanceIdentitySequencesAsync(NpgsqlConnection postgres, CancellationToken cancellationToken)
    {
        var identities = new List<(string Table, string Column)>();
        await using (var find = postgres.CreateCommand())
        {
            find.CommandText =
                "SELECT table_name, column_name FROM information_schema.columns "
                + "WHERE table_schema = current_schema() AND is_identity = 'YES' ORDER BY table_name, column_name";
            await using var reader = await find.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                identities.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach (var (table, column) in identities)
        {
            await ExecuteAsync(postgres,
                $"SELECT setval(pg_get_serial_sequence({Literal(Quote(table))}, {Literal(column)}), "
                + $"COALESCE((SELECT MAX({Quote(column)}) FROM {Quote(table)}), 0) + 1, false)",
                cancellationToken);
        }
    }

    private static object? ReadSqlite(SqliteDataReader reader, int ordinal, Type type)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        if (type == typeof(string)) return reader.GetString(ordinal);
        if (type == typeof(long)) return reader.GetInt64(ordinal);
        if (type == typeof(int)) return reader.GetInt32(ordinal);
        if (type == typeof(short)) return reader.GetInt16(ordinal);
        if (type == typeof(bool)) return reader.GetBoolean(ordinal);
        if (type == typeof(DateTimeOffset))
        {
            // EF Core's SQLite provider stored "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz".
            var value = DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            return ToMicroseconds(value.ToUniversalTime());
        }

        if (type == typeof(DateTime))
        {
            // Stored without a zone; every such column holds UTC.
            var value = DateTime.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            return ToMicroseconds(value);
        }

        throw new InvalidOperationException($"Provider type {type.Name} has no legacy SQLite import mapping.");
    }

    private static object? ReadPostgres(NpgsqlDataReader reader, int ordinal, Type type)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        if (type == typeof(string)) return reader.GetString(ordinal);
        if (type == typeof(long)) return reader.GetInt64(ordinal);
        if (type == typeof(int)) return reader.GetInt32(ordinal);
        if (type == typeof(short)) return reader.GetInt16(ordinal);
        if (type == typeof(bool)) return reader.GetBoolean(ordinal);
        if (type == typeof(DateTimeOffset)) return reader.GetFieldValue<DateTimeOffset>(ordinal);
        if (type == typeof(DateTime)) return reader.GetFieldValue<DateTime>(ordinal);
        throw new InvalidOperationException($"Provider type {type.Name} has no legacy SQLite import mapping.");
    }

    private static DateTimeOffset ToMicroseconds(DateTimeOffset value) =>
        new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);

    private static DateTime ToMicroseconds(DateTime value) =>
        new(value.Ticks - value.Ticks % 10, DateTimeKind.Utc);

    /// <summary>
    /// SHA-256 over a typed, length-prefixed encoding of one row, folded to
    /// 128 bits. Integers of every width encode alike, because SQLite does
    /// not distinguish them; instants encode as UTC ticks.
    /// </summary>
    private sealed class RowDigest
    {
        private readonly ArrayBufferWriter<byte> _buffer = new();

        public UInt128 Of(object?[] values)
        {
            _buffer.ResetWrittenCount();
            foreach (var value in values)
            {
                switch (value)
                {
                    case null:
                        Tag(0);
                        break;
                    case string text:
                        Tag(1);
                        var length = Encoding.UTF8.GetByteCount(text);
                        Int64(length);
                        Encoding.UTF8.GetBytes(text, _buffer.GetSpan(length));
                        _buffer.Advance(length);
                        break;
                    case long number:
                        Tag(2);
                        Int64(number);
                        break;
                    case int number:
                        Tag(2);
                        Int64(number);
                        break;
                    case short number:
                        Tag(2);
                        Int64(number);
                        break;
                    case bool flag:
                        Tag(flag ? (byte)4 : (byte)3);
                        break;
                    case DateTimeOffset instant:
                        Tag(5);
                        Int64(instant.UtcTicks);
                        break;
                    case DateTime instant:
                        Tag(5);
                        Int64(instant.ToUniversalTime().Ticks);
                        break;
                    default:
                        throw new InvalidOperationException($"Value type {value.GetType().Name} has no digest encoding.");
                }
            }

            Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
            SHA256.HashData(_buffer.WrittenSpan, hash);
            return BinaryPrimitives.ReadUInt128LittleEndian(hash);
        }

        private void Tag(byte tag)
        {
            _buffer.GetSpan(1)[0] = tag;
            _buffer.Advance(1);
        }

        private void Int64(long value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(_buffer.GetSpan(8), value);
            _buffer.Advance(8);
        }
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Sqlite(string path, SqliteOpenMode mode) =>
        new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString();

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static LegacySqliteImportException Refuse(string message) => new(message);

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("A temporary SQLite working copy could not be deleted from the data directory.");
        }
    }
}
