using Microsoft.Data.Sqlite;
using ServiceMantle.Database.Sqlite;

namespace Lexarbor.Host;

internal static class SqliteConnectionConfiguration
{
    internal static string Build(string? configuredConnectionString, string contentRootPath)
    {
        SqliteConnectionStringBuilder builder;
        try
        {
            builder = new SqliteConnectionStringBuilder(
                string.IsNullOrWhiteSpace(configuredConnectionString)
                    ? "Data Source=data/vocabulary.db"
                    : configuredConnectionString);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw InvalidConfiguration();
        }

        if (string.IsNullOrWhiteSpace(builder.DataSource))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:Default must define a SQLite data source.");
        }

        // Keep the product's bounded write wait, including explicitly supplied
        // 30 seconds. Every other parameter passes to the shared resolver.
        if (builder.DefaultTimeout == 30)
        {
            builder.DefaultTimeout = 5;
        }

        try
        {
            return SqliteDataSource.ResolveConnectionString(builder.ToString(), contentRootPath);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            // Provider exceptions can carry the submitted string in their inner
            // exception. Keep only a fixed safe diagnostic at the Host boundary.
            throw InvalidConfiguration();
        }
    }

    private static InvalidOperationException InvalidConfiguration() =>
        new("ConnectionStrings:Default must be a valid SQLite connection string with a supported data source.");
}
