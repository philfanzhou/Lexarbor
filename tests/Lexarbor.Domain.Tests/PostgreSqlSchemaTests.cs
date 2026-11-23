using Lexarbor.Database;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Lexarbor.Domain.Tests;

/// <summary>
/// The PostgreSQL baseline schema: the rules the SQLite schema enforced with
/// constraints and triggers, the byte-wise text semantics, and the ASCII-only
/// normalization, each checked against a real server.
/// </summary>
public sealed class PostgreSqlSchemaTests
{
    private static CancellationToken Token => AudioTestDatabase.Token;

    [Fact]
    public async Task Baseline_HasNoPendingModelChanges_AndEveryTextColumnIsByteOrdered()
    {
        await using var database = new PostgreSqlTestDatabase();
        await using var db = database.Open();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Token));

        await using var connection = await database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT table_name || '.' || column_name FROM information_schema.columns
            WHERE table_schema = 'public' AND data_type = 'text' AND table_name <> '__EFMigrationsHistory'
              AND collation_name IS DISTINCT FROM 'C'
            """, connection);
        var uncollated = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(Token))
            while (await reader.ReadAsync(Token)) uncollated.Add(reader.GetString(0));
        Assert.Empty(uncollated);
    }

    [Fact]
    public async Task NormalizedColumns_FoldAsciiOnly_SoNonAsciiCaseVariantsStayDistinct()
    {
        await using var database = new PostgreSqlTestDatabase();
        await using var db = database.Open();
        db.VocabularyBooks.Add(new() { Id = "book", BookName = "Book", Status = true });
        db.Vocabularies.AddRange(
            new() { Id = "upper", Word = "  CAFÉ " },
            new() { Id = "title", Word = "Café" },
            new() { Id = "ascii", Word = "Apple" });
        await db.SaveChangesAsync(Token);
        db.VocabularyMeanings.AddRange(
            new() { Id = "m1", VocabularyId = "upper", BookId = "book", PartOfSpeech = " ÉTAT N. ", Meaning = " drink " },
            new() { Id = "m2", VocabularyId = "upper", BookId = "book", PartOfSpeech = "état n.", Meaning = "drink" });
        await db.SaveChangesAsync(Token);
        db.ChangeTracker.Clear();

        var words = await db.Vocabularies.AsNoTracking().OrderBy(word => word.Id)
            .Select(word => new { word.Id, word.NormalizedWord }).ToListAsync(Token);
        Assert.Equal(["apple", "café", "cafÉ"], words.Select(word => word.NormalizedWord));
        var meanings = await db.VocabularyMeanings.AsNoTracking().OrderBy(meaning => meaning.Id)
            .Select(meaning => new { meaning.NormalizedPartOfSpeech, meaning.NormalizedMeaning }).ToListAsync(Token);
        // Two meanings whose part of speech differs only in the case of a
        // non-ASCII letter are two keys, exactly as SQLite's lower() kept them.
        Assert.Equal(("État n.", "drink"), (meanings[0].NormalizedPartOfSpeech, meanings[0].NormalizedMeaning));
        Assert.Equal(("état n.", "drink"), (meanings[1].NormalizedPartOfSpeech, meanings[1].NormalizedMeaning));

        // lower() over a "C" column folds ASCII only, which raw SQL relies on.
        var lowered = await db.Database.SqlQuery<string>($"SELECT lower(word) AS \"Value\" FROM vocabulary WHERE id = 'upper'").SingleAsync(Token);
        Assert.Equal("  cafÉ ", lowered);
    }

    [Fact]
    public async Task WordOrder_IsByteOrder_WhateverTheDatabaseCollation()
    {
        await using var database = new PostgreSqlTestDatabase();
        await using var db = database.Open();
        foreach (var word in new[] { "apple", "Ápple", "Zebra", "zebra", "_under", "Apple" })
            db.Vocabularies.Add(new() { Id = Guid.NewGuid().ToString(), Word = word });
        await db.SaveChangesAsync(Token);

        var ordered = await db.Vocabularies.AsNoTracking().OrderBy(word => word.Word).Select(word => word.Word).ToListAsync(Token);
        Assert.Equal(["Apple", "Zebra", "_under", "apple", "zebra", "Ápple"], ordered);
        var page = await db.Vocabularies.AsNoTracking().OrderBy(word => word.Word).Skip(2).Take(2).Select(word => word.Word).ToListAsync(Token);
        Assert.Equal(["_under", "apple"], page);
    }

    [Fact]
    public async Task InsertionOrder_IsAssignedOnInsert_AndIndexed()
    {
        await using var database = new PostgreSqlTestDatabase();
        await using var db = database.Open();
        foreach (var id in new[] { "z", "a", "m" })
        {
            db.Vocabularies.Add(new() { Id = id, Word = id });
            await db.SaveChangesAsync(Token);
        }

        var newestFirst = await db.Vocabularies.FromSqlRaw("SELECT * FROM vocabulary ORDER BY insertion_order DESC")
            .AsNoTracking().Select(word => word.Id).ToListAsync(Token);
        Assert.Equal(["m", "a", "z"], newestFirst);

        await using var connection = await database.OpenConnectionAsync();
        foreach (var table in new[] { "vocabulary", "vocabulary_meaning" })
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_indexes WHERE tablename = @table AND indexdef LIKE '%(insertion_order)%'", connection);
            command.Parameters.AddWithValue("table", table);
            Assert.Equal(1L, await command.ExecuteScalarAsync(Token));
        }
    }

    [Fact]
    public async Task EnrichmentCalls_AreIndexedByTargetInStartOrder()
    {
        await using var database = new PostgreSqlTestDatabase();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT indexdef FROM pg_indexes WHERE tablename = 'enrichment_call' AND indexname = 'IX_enrichment_call_target_id_started_at_unix_ms'", connection);

        var definition = Assert.IsType<string>(await command.ExecuteScalarAsync(Token));
        Assert.EndsWith("(target_id, started_at_unix_ms)", definition);
        Assert.DoesNotContain("UNIQUE", definition);
        Assert.DoesNotContain("WHERE", definition);
    }

    [Fact]
    public async Task UniqueViolation_IsReportedAsTheExistingConflict()
    {
        await using var database = new PostgreSqlTestDatabase();
        await using var db = database.Open();
        db.Vocabularies.Add(new() { Id = "one", Word = "same" });
        await db.SaveChangesAsync(Token);
        db.ChangeTracker.Clear();

        db.Vocabularies.Add(new() { Id = "two", Word = "same" });
        var conflict = await Assert.ThrowsAsync<ConflictException>(() => new UnitOfWork(db).SaveChangesAsync(Token));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(conflict.InnerException?.InnerException ?? conflict.InnerException).SqlState);
    }

    [Fact]
    public async Task LockWaitPastTheTimeout_IsReportedAsBusy()
    {
        await using var database = new PostgreSqlTestDatabase();
        await using (var seed = database.Open())
        {
            seed.Vocabularies.Add(new() { Id = "held", Word = "held" });
            await seed.SaveChangesAsync(Token);
        }

        await using var holder = await database.OpenConnectionAsync();
        await using var transaction = await holder.BeginTransactionAsync(Token);
        await using (var lockRow = new NpgsqlCommand("SELECT 1 FROM vocabulary WHERE id = 'held' FOR UPDATE", holder, transaction))
            await lockRow.ExecuteScalarAsync(Token);

        var options = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Options = "-c lock_timeout=200ms" };
        await using var db = new VocabularyDbContext(PostgreSqlTestServer.Options(options.ConnectionString).Options);
        var row = await db.Vocabularies.SingleAsync(word => word.Id == "held", Token);
        row.Word = "changed";
        await Assert.ThrowsAsync<StorageBusyException>(() => new UnitOfWork(db).SaveChangesAsync(Token));
    }

    [Fact]
    public async Task ReadSnapshot_RefusesWrites()
    {
        await using var database = new PostgreSqlTestDatabase();
        await using var db = database.Open();
        await db.Database.OpenConnectionAsync(Token);
        await using var snapshot = await ReadSnapshot.BeginAsync(db, Token);
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync("INSERT INTO vocabulary (id, word, created_at, updated_at) VALUES ('x', 'x', now(), now())", Token));
        Assert.Equal(PostgresErrorCodes.ReadOnlySqlTransaction, error.SqlState);
    }

    [Fact]
    public async Task AudioConstraints_RejectDuplicateTargetsWrongOwnershipInvalidLocaleAndBrokenReceipts()
    {
        using var file = new AudioTestDatabase();
        await using var db = file.Open();
        await AudioTestDatabase.SeedAsync(db);
        foreach (var id in new[] { "gb", "us", "meaning-gb", "meaning-us" }) db.AudioAssets.Add(AudioTestDatabase.Asset(id));
        db.VocabularyAudioBindings.AddRange(AudioTestDatabase.Binding("gb", AudioLocale.EnGb, null, AudioTestDatabase.Hash),
            AudioTestDatabase.Binding("us", AudioLocale.EnUs, null, AudioTestDatabase.Hash),
            AudioTestDatabase.Binding("meaning-gb", AudioLocale.EnGb, "meaning", AudioTestDatabase.Hash),
            AudioTestDatabase.Binding("meaning-us", AudioLocale.EnUs, "meaning", AudioTestDatabase.Hash));
        await db.SaveChangesAsync(Token);
        Assert.Equal(4, await db.VocabularyAudioBindings.CountAsync(Token));
        foreach (var target in new (string? Meaning, string Locale)[] { (null, "en-GB"), ("meaning", "en-US"), (null, "en-AU"), ("other-meaning", "en-GB"), ("missing", "en-US") })
        {
            await AssertIntegrityViolationAsync(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO vocabulary_audio_binding (id, word_id, meaning_id, locale, asset_id, source_revision) VALUES ('invalid','word',{target.Meaning},{target.Locale},'gb',{AudioTestDatabase.Hash})", Token));
        }
        foreach (var state in Enum.GetValues<AudioUploadState>())
        {
            var id = state.ToString(); db.AudioAssets.Add(AudioTestDatabase.Asset(id, state == AudioUploadState.Succeeded));
            db.AudioUploadOperations.Add(AudioTestDatabase.Operation(id, state));
        }
        await db.SaveChangesAsync(Token);
        Assert.Equal(6, await db.AudioUploadOperations.CountAsync(Token));
        foreach (var update in new[] { "state='unknown'", "locale='en-AU'", "receipt_status_code=NULL", "receipt_asset_id=NULL", "receipt_source_revision=NULL", "completed_at_unix_ms=NULL" })
        {
            // Fragments are fixed test cases, never request input.
            var sql = $"UPDATE audio_upload_operation SET {update} WHERE state='Succeeded'";
            await AssertIntegrityViolationAsync(() => db.Database.ExecuteSqlRawAsync(sql, Token));
        }
        await AssertIntegrityViolationAsync(() => db.Database.ExecuteSqlRawAsync("UPDATE audio_upload_operation SET upload_lease_owner=NULL WHERE state='Reserved'", Token));
        await AssertIntegrityViolationAsync(() => db.Database.ExecuteSqlRawAsync("UPDATE audio_upload_operation SET cleanup_lease_fence=0 WHERE state='CleanupClaimed'", Token));
        await AssertIntegrityViolationAsync(() => db.Database.ExecuteSqlRawAsync("DELETE FROM audio_asset WHERE id='gb'", Token));
        await AssertIntegrityViolationAsync(() => db.Database.ExecuteSqlRawAsync("DELETE FROM audio_asset WHERE id='Abandoned'", Token));
        await AssertIntegrityViolationAsync(() => db.Database.ExecuteSqlRawAsync("UPDATE audio_asset SET sha256=upper(sha256) WHERE id='gb'", Token));
        await db.Database.ExecuteSqlRawAsync("DELETE FROM vocabulary_meaning WHERE id='meaning'", Token);
        Assert.Equal(2, await db.VocabularyAudioBindings.CountAsync(Token));
        await db.Database.ExecuteSqlRawAsync("DELETE FROM vocabulary WHERE id='word'", Token);
        Assert.Empty(await db.VocabularyAudioBindings.ToListAsync(Token));
        Assert.Equal(10, await db.AudioAssets.CountAsync(Token));
        Assert.Equal(6, await db.AudioUploadOperations.CountAsync(Token));
    }

    [Fact]
    public async Task AudioInvalidationTriggers_GuardAuthorityTombstonesAndDeletedAudio()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await AudioTestDatabase.SeedAsync(db);
        db.AudioAssets.Add(AudioTestDatabase.Asset("published"));
        db.AudioAssets.Add(AudioTestDatabase.Asset("unpublished", published: false));
        db.VocabularyAudioBindings.Add(AudioTestDatabase.Binding("published", AudioLocale.EnGb, "meaning", AudioTestDatabase.Hash));
        await db.SaveChangesAsync(Token); db.ChangeTracker.Clear();
        await new UnitOfWork(db).ExecuteInTransactionAsync(() => new AudioInvalidationRepository(db).AuthorizeInTransactionAsync(new("word", "meaning", AudioLocale.EnGb, "published", 1), 10, Token));
        Assert.Equal(1, await db.AudioInvalidations.CountAsync(Token));

        // An insert without a matching published binding is not an authority.
        await AssertIntegrityViolationAsync(() => db.Database.ExecuteSqlRawAsync("""
            INSERT INTO audio_invalidation (asset_id, bucket, object_key, word_id, meaning_id, locale, authorization_epoch, reason, state,
              attempts, lease_fence, next_attempt_at_unix_ms, created_at_unix_ms, updated_at_unix_ms)
            SELECT 'unpublished', bucket, object_key, 'word', 'meaning', 'en-GB', 1, 'specific_phonetic_changed', 'Waiting', 0, 0, 0, 1, 1
            FROM audio_asset WHERE id = 'unpublished'
            """, Token));
        // The authority, its location, and the tombstone are permanent.
        await AssertIntegrityViolationAsync(() => db.Database.ExecuteSqlRawAsync("UPDATE audio_invalidation SET word_id='other'", Token));
        await AssertIntegrityViolationAsync(() => db.Database.ExecuteSqlRawAsync("UPDATE audio_asset SET object_key='moved' WHERE id='published'", Token));
        await AssertIntegrityViolationAsync(() => db.Database.ExecuteSqlRawAsync("DELETE FROM audio_invalidation", Token));
        // A referenced asset cannot be claimed for deletion.
        await AssertIntegrityViolationAsync(() => db.Database.ExecuteSqlRawAsync("UPDATE audio_invalidation SET delete_started_at_unix_ms=20", Token));
        Assert.True(await new AudioRecordExistenceRepository(db).HasAnyAsync(Token));
    }

    [Fact]
    public async Task EnrichmentIndexesAndChecks_RejectNullDuplicatesAndInvalidStates()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await AudioTestDatabase.SeedAsync(db);
        await db.Database.ExecuteSqlRawAsync("UPDATE vocabulary_meaning SET phonetic_uk='specific' WHERE id='meaning'", Token);
        var repo = new EnrichmentTaskRepository(db);
        foreach (var meaning in new string?[] { null, "meaning" })
        {
            var task = Assert.IsType<EnrichmentTask>(await repo.EnsureAsync(new("word", meaning, AudioLocale.EnGb, EnrichmentStep.Audio), "config", 1, Token));
            await AssertIntegrityViolationAsync(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO enrichment_target(id,word_id,meaning_id,locale,step,current_round,epoch,deleted)
                SELECT {Guid.NewGuid().ToString()},word_id,meaning_id,locale,step,1,1,false FROM enrichment_target WHERE id={task.Id}
                """, Token));
        }
        foreach (var sql in new[] {
            "UPDATE enrichment_round SET consumed_failures=6",
            "UPDATE enrichment_round SET request_count=-1",
            "UPDATE enrichment_round SET state='Running'",
            "UPDATE enrichment_target SET locale='fr-FR'",
            "UPDATE enrichment_target SET step='Fields' WHERE meaning_id IS NOT NULL" })
            await AssertIntegrityViolationAsync(() => db.Database.ExecuteSqlRawAsync(sql, Token));
        Assert.Equal(2, await db.EnrichmentTargets.CountAsync(Token));

        // Deleting the word cancels its open work through the trigger.
        await db.Database.ExecuteSqlRawAsync("DELETE FROM vocabulary WHERE id='word'", Token);
        Assert.All(await db.EnrichmentTargets.AsNoTracking().ToListAsync(Token), target => Assert.True(target.Deleted));
        Assert.All(await db.EnrichmentRounds.AsNoTracking().ToListAsync(Token), round => Assert.Equal(EnrichmentState.Cancelled, round.State));
    }

    [Fact]
    public async Task RelationSchema_EnforcesIntegrity()
    {
        await using var database = new PostgreSqlTestDatabase();
        await using var db = database.Open();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO vocabulary(id, word, created_at, updated_at) VALUES ('cut', 'cut', now(), now());
            INSERT INTO vocabulary(id, word, created_at, updated_at) VALUES ('write', 'write', now(), now());
            """, Token);
        foreach (var type in VocabularyRelationTypes.All)
            await InsertRelation(db, type.Code, type.Code);
        Assert.Equal(11, await db.VocabularyRelations.CountAsync(Token));
        var defaults = await db.VocabularyRelations.AsNoTracking().FirstAsync(Token);
        Assert.Equal(("needs_review", "new_relation", 1L), (defaults.Status, defaults.ReviewReason, defaults.Version));
        Assert.Null(defaults.LastConfirmedAtUnixMs);
        await AssertIntegrityViolationAsync(() => InsertRelation(db, "duplicate", "past_tense"));
        await AssertIntegrityViolationAsync(() => InsertRelation(db, "unknown", "base_form"));
        await AssertIntegrityViolationAsync(() => db.Database.ExecuteSqlRawAsync("DELETE FROM vocabulary WHERE id='cut'", Token));
        await AssertIntegrityViolationAsync(() => db.Database.ExecuteSqlRawAsync("UPDATE vocabulary SET id='changed' WHERE id='cut'", Token));
        foreach (var assignment in new[] { "source_word_id='missing'", "target_word_id='missing'", "status='confirmed'",
                     "review_reason=NULL", "version=0", "updated_at_unix_ms=-1", "usage_note=' '", "origin_kind='unknown'" })
        {
            // Assignment fragments above are test constants, never external inputs.
            var sql = $"UPDATE vocabulary_relation SET {assignment} WHERE id='past_tense'";
            await AssertIntegrityViolationAsync(() => db.Database.ExecuteSqlRawAsync(sql, Token));
        }
    }

    [Fact]
    public async Task AiReviewConclusions_AreImmutableInTheDatabase()
    {
        await using var database = new PostgreSqlTestDatabase();
        await using var db = database.Open();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO meaning_submission (id, source, word, meaning, status, created_at_unix_ms, updated_at_unix_ms)
            VALUES ('s', 'test', 'word', 'meaning', 0, 1, 1);
            INSERT INTO meaning_submission_ai_review (id, attempt_id, submission_id, verdict, model, reviewed_at_unix_ms, applied)
            VALUES ('r', 'a', 's', 'accept', 'model', 1, false);
            """, Token);
        await AssertIntegrityViolationAsync(() => db.Database.ExecuteSqlRawAsync("UPDATE meaning_submission_ai_review SET verdict='reject'", Token));
        await AssertIntegrityViolationAsync(() => db.Database.ExecuteSqlRawAsync("DELETE FROM meaning_submission_ai_review", Token));
    }

    private static Task<int> InsertRelation(VocabularyDbContext db, string id, string type) => db.Database.ExecuteSqlRawAsync(
        "INSERT INTO vocabulary_relation(id,source_word_id,target_word_id,type,origin_kind,created_at_unix_ms,updated_at_unix_ms) VALUES ({0},'cut','cut',{1},'manual',1,1)", [id, type], Token);

    private static async Task AssertIntegrityViolationAsync(Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(action);
        Assert.StartsWith("23", error.SqlState, StringComparison.Ordinal);
    }
}
