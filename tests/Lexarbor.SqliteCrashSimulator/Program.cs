// Usage: Lexarbor.SqliteCrashSimulator <database-path> <marker-path> [mode]
//
// A crash simulator for the SQLite startup-gate integration tests. Two shapes:
//
// - "wal" (default): it opens a connection to a WAL database, commits one
//   unmistakable row, announces readiness through the marker file, and then
//   blocks forever. The parent test SIGKILLs the process at that point, which
//   is the only faithful way to leave the committed row in the -wal sidecar
//   with no live holder — a clean Close would checkpoint and delete the
//   sidecar, and an abandoned connection inside the test process would keep
//   the file open.
//
// - "hot-journal": it opens a connection to a rollback-journal database,
//   starts an explicit IMMEDIATE transaction, writes one row without
//   committing — the hot journal file now holds the original page images —
//   announces readiness, and blocks forever. The SIGKILL leaves the
//   -journal file behind, which the next open rolls back.
//
// The process intentionally never returns after the marker exists.

using Microsoft.Data.Sqlite;

if (args.Length is < 2 or > 3)
{
    Console.Error.WriteLine("usage: Lexarbor.SqliteCrashSimulator <database-path> <marker-path> [wal|hot-journal]");
    return 2;
}

var databasePath = args[0];
var markerPath = args[1];
var mode = args.Length == 3 ? args[2] : "wal";

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
    if (mode == "hot-journal")
    {
        using var begin = connection.CreateCommand();
        begin.CommandText = "BEGIN IMMEDIATE;";
        begin.ExecuteNonQuery();
    }
    else if (mode != "wal")
    {
        Console.Error.WriteLine($"unknown mode: {mode}");
        return 2;
    }

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
