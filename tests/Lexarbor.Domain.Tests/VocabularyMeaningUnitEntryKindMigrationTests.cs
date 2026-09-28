using System;
using System.Linq;
using System.Threading.Tasks;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Lexarbor.Domain.Tests;

/// <summary>
/// Covers the upgrade path for <c>AddMeaningUnitEntryKinds</c>: a database at
/// the previous schema keeps every existing assignment — sectioned or not, all
/// as the unclassified kind — and can store kinded assignments afterwards; a
/// database created from scratch by the full migration chain builds the same
/// structure; and the Down migration merges a meaning's kinds of one place back
/// to the one row the previous key allows.
/// </summary>
public sealed class VocabularyMeaningUnitEntryKindMigrationTests : IDisposable
{
    private const string PreviousMigration = "20260928113247_AddMeaningUnitSections";

    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"lexarbor-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Upgrade_KeepsAssignmentsAsUnclassified_AndStoresKindsAfterwards()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var databasePath = Path.Combine(_temporaryDirectory, "upgraded.db");

        await using (var context = CreateContext(databasePath))
        {
            context.GetService<IMigrator>().Migrate(PreviousMigration);

            // Stands in for a database an earlier release filled in: one book,
            // one word, two meanings, one unit, three positions — written with
            // raw SQL because the entry_kind column the entity now carries
            // does not exist yet at this schema.
            var now = DateTimeOffset.UtcNow;
            context.VocabularyBooks.Add(new VocabularyBookEntity
            {
                Id = "book-a",
                BookName = "Book A",
                Status = true,
                CreatedAt = now,
                UpdatedAt = now
            });
            context.Vocabularies.Add(new VocabularyEntity
            {
                Id = "word-apple",
                Word = "apple",
                CreatedAt = now,
                UpdatedAt = now
            });
            context.VocabularyMeanings.AddRange(
                new VocabularyMeaningEntity
                {
                    Id = "meaning-1",
                    VocabularyId = "word-apple",
                    BookId = "book-a",
                    PartOfSpeech = "n.",
                    Meaning = "苹果",
                    CreatedAt = now,
                    UpdatedAt = now
                },
                new VocabularyMeaningEntity
                {
                    Id = "meaning-2",
                    VocabularyId = "word-apple",
                    BookId = "book-a",
                    PartOfSpeech = "n.",
                    Meaning = "另一种苹果",
                    CreatedAt = now,
                    UpdatedAt = now
                });
            context.VocabularyBookUnits.AddRange(
                new VocabularyBookUnitEntity
                {
                    Id = "unit-2",
                    BookId = "book-a",
                    Number = 2,
                    CreatedAt = now,
                    UpdatedAt = now
                },
                new VocabularyBookUnitEntity
                {
                    Id = "unit-6",
                    BookId = "book-a",
                    Number = 6,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            await context.Database.ExecuteSqlRawAsync("""
                INSERT INTO vocabulary_meaning_unit (unit_id, meaning_id, book_id, section) VALUES
                ('unit-2', 'meaning-1', 'book-a', ''),
                ('unit-2', 'meaning-1', 'book-a', 'A'),
                ('unit-6', 'meaning-1', 'book-a', 'B'),
                ('unit-2', 'meaning-2', 'book-a', '');
                """, TestContext.Current.CancellationToken);
        }

        // The previous schema has no entry_kind column, so the pre-migration
        // read selects the four columns that exist; the post-migration read
        // below adds the new one.
        var before = await ReadRowsAsync(databasePath, "SELECT unit_id, meaning_id, book_id, section FROM vocabulary_meaning_unit ORDER BY unit_id, meaning_id, section", 4);

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        // Every assignment crossed the upgrade as the unclassified kind, with
        // its book and section untouched.
        var after = await ReadRowsAsync(databasePath, "SELECT unit_id, meaning_id, book_id, section, entry_kind FROM vocabulary_meaning_unit ORDER BY unit_id, meaning_id, section", 5);
        Assert.Equal(before.Select(ToKey), after.Select(columns => ToKey([columns[0], columns[1], columns[2], columns[3]])));
        Assert.All(after, columns => Assert.Equal(string.Empty, columns[4]));

        // The rebuilt table carries the constraint set: the wider key, both
        // domain CHECKs, and both membership indexes. The exact schema string
        // is compared with a from-scratch database in the test below.
        var schema = await ReadSchemaAsync(databasePath, "vocabulary_meaning_unit");
        Assert.Contains("PRIMARY KEY (\"unit_id\", \"meaning_id\", \"section\", \"entry_kind\")", schema);
        Assert.Contains("CHECK (section IN ('', 'A', 'B'))", schema);
        Assert.Contains("CHECK (entry_kind IN ('', 'word', 'phrase'))", schema);
        Assert.Equal(
            ["IX_vocabulary_meaning_unit_meaning_id_book_id", "IX_vocabulary_meaning_unit_unit_id_book_id"],
            (await ReadIndexNamesAsync(databasePath)).Order());
        // The CHECK constraint is live in the database, not only in the model:
        // a writer past the application layer cannot store another value.
        await using (var context = CreateContext(databasePath))
        {
            await Assert.ThrowsAsync<SqliteException>(
                () => context.Database.ExecuteSqlRawAsync(
                    "INSERT INTO vocabulary_meaning_unit (unit_id, meaning_id, book_id, section, entry_kind) VALUES ('unit-2', 'meaning-2', 'book-a', '', 'noun')",
                    TestContext.Current.CancellationToken));
            // A repeated unclassified assignment is a conflict, not a second row.
            await Assert.ThrowsAsync<SqliteException>(
                () => context.Database.ExecuteSqlRawAsync(
                    "INSERT INTO vocabulary_meaning_unit (unit_id, meaning_id, book_id, section, entry_kind) VALUES ('unit-2', 'meaning-2', 'book-a', '', '')",
                    TestContext.Current.CancellationToken));
        }

        // The upgraded database stores kinded positions through the domain
        // service: both kinds of one sectioned place, idempotent on replay.
        await using (var context = CreateContext(databasePath))
        {
            var unitOfWork = new UnitOfWork(context);
            var unitService = new VocabularyBookUnitDomainService(
                new VocabularyBookRepository(context),
                new VocabularyMeaningRepository(context),
                new VocabularyBookUnitRepository(context),
                new VocabularyMeaningUnitRepository(context),
                unitOfWork);

            await unitService.AssignMeaningAsync("unit-2", "meaning-1", "A", "word");
            await unitService.AssignMeaningAsync("unit-2", "meaning-1", "A", "phrase");
            await unitService.AssignMeaningAsync("unit-2", "meaning-1", "A", "word");
            var stored = await context.VocabularyMeaningUnits.AsNoTracking()
                .Where(membership => membership.MeaningId == "meaning-1" && membership.UnitId == "unit-2" && membership.Section == "A")
                .Select(membership => membership.EntryKind)
                .ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(["", "phrase", "word"], stored.Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task NewDatabase_BuiltByTheFullMigrationChain_MatchesTheUpgradedStructure()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var freshPath = Path.Combine(_temporaryDirectory, "fresh.db");
        var upgradedPath = Path.Combine(_temporaryDirectory, "upgraded-structure.db");

        await using (var context = CreateContext(freshPath))
        {
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        await using (var context = CreateContext(upgradedPath))
        {
            context.GetService<IMigrator>().Migrate(PreviousMigration);
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        var freshTable = await ReadSchemaAsync(freshPath, "vocabulary_meaning_unit");
        var upgradedTable = await ReadSchemaAsync(upgradedPath, "vocabulary_meaning_unit");
        Assert.Equal(freshTable, upgradedTable);
        Assert.Equal(
            await ReadIndexNamesAsync(freshPath),
            (await ReadIndexNamesAsync(upgradedPath)).Order());
        Assert.Equal(
            await ReadColumnTypesAsync(freshPath),
            await ReadColumnTypesAsync(upgradedPath));
    }

    [Fact]
    public async Task Down_MergesMultiKindPositions_ToOneRowPerPlace()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var databasePath = Path.Combine(_temporaryDirectory, "down.db");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        await using (var context = CreateContext(databasePath))
        {
            var now = DateTimeOffset.UtcNow;
            context.VocabularyBooks.Add(new VocabularyBookEntity
            {
                Id = "book-a",
                BookName = "Book A",
                Status = true,
                CreatedAt = now,
                UpdatedAt = now
            });
            context.Vocabularies.Add(new VocabularyEntity
            {
                Id = "word-apple",
                Word = "apple",
                CreatedAt = now,
                UpdatedAt = now
            });
            context.VocabularyMeanings.Add(new VocabularyMeaningEntity
            {
                Id = "meaning-1",
                VocabularyId = "word-apple",
                BookId = "book-a",
                Meaning = "苹果",
                CreatedAt = now,
                UpdatedAt = now
            });
            context.VocabularyMeanings.Add(new VocabularyMeaningEntity
            {
                Id = "meaning-2",
                VocabularyId = "word-apple",
                BookId = "book-a",
                Meaning = "另一种苹果",
                CreatedAt = now,
                UpdatedAt = now
            });
            context.VocabularyBookUnits.Add(new VocabularyBookUnitEntity
            {
                Id = "unit-2",
                BookId = "book-a",
                Number = 2,
                CreatedAt = now,
                UpdatedAt = now
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            // One meaning in all three kinds of one place, and a second
            // meaning in one: the previous schema keeps one row of each.
            await context.Database.ExecuteSqlRawAsync("""
                INSERT INTO vocabulary_meaning_unit (unit_id, meaning_id, book_id, section, entry_kind) VALUES
                ('unit-2', 'meaning-1', 'book-a', 'A', 'word'),
                ('unit-2', 'meaning-1', 'book-a', 'A', 'phrase'),
                ('unit-2', 'meaning-1', 'book-a', 'A', ''),
                ('unit-2', 'meaning-2', 'book-a', 'B', 'phrase');
                """, TestContext.Current.CancellationToken);
        }

        await using (var context = CreateContext(databasePath))
        {
            context.GetService<IMigrator>().Migrate(PreviousMigration);
        }

        // The previous schema has no entry_kind column, so the check reads the
        // four columns that exist: one row per (unit, meaning, section)
        // survived the merge, each with its section intact.
        var rows = await ReadRowsAsync(databasePath, "SELECT unit_id, meaning_id, book_id, section FROM vocabulary_meaning_unit ORDER BY section", 4);
        Assert.Equal(
            [["unit-2", "meaning-1", "book-a", "A"], ["unit-2", "meaning-2", "book-a", "B"]],
            rows);
    }

    private static string ToKey(string[] columns) => $"{columns[0]}|{columns[1]}|{columns[2]}|{columns[3]}";

    private static async Task<List<string[]>> ReadRowsAsync(string databasePath, string sql, int columnCount)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<string[]>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            var columns = new string[columnCount];
            for (var index = 0; index < columnCount; index++)
            {
                columns[index] = reader.GetString(index);
            }

            rows.Add(columns);
        }

        return rows;
    }

    private static async Task<string> ReadSchemaAsync(string databasePath, string table)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = $table";
        command.Parameters.AddWithValue("$table", table);
        return (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<List<string>> ReadIndexNamesAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'vocabulary_meaning_unit' AND name LIKE 'IX_%'";
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var names = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<List<(string Name, string Type, bool NotNull, object DefaultValue)>> ReadColumnTypesAsync(
        string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info('vocabulary_meaning_unit')";
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var columns = new List<(string, string, bool, object)>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            columns.Add((reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0, reader.GetValue(4)));
        }

        return columns;
    }

    public void Dispose()
    {
        var resolvedTemporaryDirectory = Path.GetFullPath(_temporaryDirectory);
        var resolvedSystemTemporaryDirectory = Path.GetFullPath(Path.GetTempPath());
        if (resolvedTemporaryDirectory.StartsWith(
                resolvedSystemTemporaryDirectory,
                StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(resolvedTemporaryDirectory))
        {
            Directory.Delete(resolvedTemporaryDirectory, recursive: true);
        }
    }

    private static VocabularyDbContext CreateContext(string databasePath)
    {
        return new VocabularyDbContext(
            new DbContextOptionsBuilder<VocabularyDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options);
    }
}
