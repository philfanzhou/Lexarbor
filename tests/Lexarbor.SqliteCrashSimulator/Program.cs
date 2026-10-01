// Usage: Lexarbor.SqliteCrashSimulator <database-path> <marker-path>
//
// A crash simulator for the SQLite startup-gate integration tests: it opens a
// write-ahead-logging connection to an already-migrated database, commits one
// unmistakable row, announces readiness through the marker file, and then
// blocks forever. The parent test SIGKILLs the process at that point, which is
// the only faithful way to leave the committed row in the -wal sidecar with no
// live holder — a clean Close would checkpoint and delete the sidecar, and an
// abandoned connection inside the test process would keep the file open. The
// process intentionally never returns after the marker exists.

using Microsoft.Data.Sqlite;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: Lexarbor.SqliteCrashSimulator <database-path> <marker-path>");
    return 2;
}

var databasePath = args[0];
var markerPath = args[1];

var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
connection.Open();
try
{
    using var command = connection.CreateCommand();
    command.CommandText = """
        INSERT INTO vocabulary (id, word, phonetic_uk, phonetic_us, created_at, updated_at)
        VALUES (
            'crash-survivor',
            'crash-survivor',
            NULL,
            NULL,
            '2026-01-01T00:00:00+00:00',
            '2026-01-01T00:00:00+00:00');
        """;
    command.ExecuteNonQuery();
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 3;
}

File.WriteAllText(markerPath, "committed");
Thread.Sleep(Timeout.Infinite);
return 0;
