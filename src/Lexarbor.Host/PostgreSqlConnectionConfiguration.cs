using System.Data.Common;
using Npgsql;

namespace Lexarbor.Host;

/// <summary>
/// Validates <c>ConnectionStrings:Default</c> as the one required PostgreSQL
/// connection string and returns the value the application connects with.
/// </summary>
/// <remarks>
/// There is no default: a missing value, a value the provider cannot parse,
/// one without a host or database, and a value that is still a SQLite
/// connection string (<c>Data Source=...</c>) each fail startup with a fixed
/// diagnostic. No diagnostic echoes the value, and no provider exception —
/// whose message or inner exception may carry the submitted string — leaves
/// this boundary.
/// </remarks>
internal static class PostgreSqlConnectionConfiguration
{
    internal const string MissingDiagnostic =
        "ConnectionStrings:Default is required and must be a PostgreSQL connection string.";

    internal const string SqliteDiagnostic =
        "ConnectionStrings:Default is a SQLite connection string. Lexarbor now requires PostgreSQL; configure a PostgreSQL connection string.";

    internal const string InvalidDiagnostic =
        "ConnectionStrings:Default must be a valid PostgreSQL connection string with a host and a database.";

    /// <summary>
    /// The bounded lock wait the SQLite provider used to apply through its
    /// five-second busy timeout. A statement that waits longer than this for a
    /// row or table lock fails with SQLSTATE 55P03, which the unit of work
    /// reports as a retryable busy answer instead of holding the request.
    /// </summary>
    internal const string DefaultLockTimeout = "5s";

    /// <summary>
    /// The planner estimates the catalog-wide administration reports far above
    /// their real cost, so with JIT on it compiles them on every refresh, and the
    /// compilation alone takes several times longer than the query. Lexarbor's
    /// data is too small for JIT to ever pay off.
    /// </summary>
    internal const string DefaultJit = "off";

    private static readonly string[] SqliteOnlyKeywords = ["Data Source", "DataSource", "Filename"];

    internal static string Build(string? configuredConnectionString)
    {
        if (string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            throw new InvalidOperationException(MissingDiagnostic);
        }

        // Recognize the previous release's value before handing it to the
        // PostgreSQL parser, so an operator who upgraded without changing the
        // setting sees what to change rather than a generic parse failure.
        DbConnectionStringBuilder generic;
        try
        {
            generic = new DbConnectionStringBuilder { ConnectionString = configuredConnectionString };
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw new InvalidOperationException(InvalidDiagnostic);
        }

        if (SqliteOnlyKeywords.Any(generic.ContainsKey))
        {
            throw new InvalidOperationException(SqliteDiagnostic);
        }

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(configuredConnectionString);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidCastException or OverflowException)
        {
            throw new InvalidOperationException(InvalidDiagnostic);
        }

        if (string.IsNullOrWhiteSpace(builder.Host) || string.IsNullOrWhiteSpace(builder.Database))
        {
            throw new InvalidOperationException(InvalidDiagnostic);
        }

        // Keep the product's bounded lock wait and JIT setting unless the
        // deployment chose its own. Every other parameter passes through unchanged.
        var options = builder.Options ?? string.Empty;
        if (!options.Contains("lock_timeout", StringComparison.OrdinalIgnoreCase))
        {
            options = Append(options, $"-c lock_timeout={DefaultLockTimeout}");
        }

        if (!options.Contains("jit", StringComparison.OrdinalIgnoreCase))
        {
            options = Append(options, $"-c jit={DefaultJit}");
        }

        builder.Options = options;

        // Npgsql otherwise opens every connection by offering GSS encryption,
        // which needs the Kerberos libraries the runtime image does not ship
        // and logs a load failure on each start. A deployment that does use
        // GSS sets the mode explicitly.
        if (!generic.ContainsKey("Gss Encryption Mode") && !generic.ContainsKey("GssEncryptionMode"))
        {
            builder.GssEncryptionMode = GssEncryptionMode.Disable;
        }

        return builder.ConnectionString;
    }

    private static string Append(string options, string option) =>
        string.IsNullOrWhiteSpace(options) ? option : $"{options} {option}";
}
