using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.Common.Database;

namespace Ruoyu.Study.Vocabulary.Database;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(
        VocabularyDbContext context,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default)
    {
        await Common.Database.DatabaseInitializer.InitializeAsync(context, loggerFactory, GetTableCreationSql);

        var logger = loggerFactory.CreateLogger("VocabularyDatabaseInitializer");
        await EnsureMeaningBookIntegrityAsync(context, logger, cancellationToken);
        await VocabularyDataIntegrityDiagnostics.InspectAndLogAsync(context, logger, cancellationToken);
    }

    internal static async Task EnsureMeaningBookIntegrityAsync(
        VocabularyDbContext context,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        await context.Database.ExecuteSqlRawAsync(
            BuildMeaningBookIntegritySql(),
            cancellationToken);

        logger.LogInformation("Vocabulary meaning-to-book integrity constraints were checked");
    }

    internal static string BuildMeaningBookIntegritySql() => """
        DO $$
        BEGIN
            IF NOT EXISTS (
                SELECT 1
                FROM pg_constraint
                WHERE conname = 'CK_vocabulary_meaning_book_id_required'
                  AND conrelid = 'vocabulary_meaning'::regclass
            ) THEN
                ALTER TABLE vocabulary_meaning
                    ADD CONSTRAINT "CK_vocabulary_meaning_book_id_required"
                    CHECK (book_id IS NOT NULL) NOT VALID;
            END IF;

            IF NOT EXISTS (
                SELECT 1
                FROM pg_constraint
                WHERE conname = 'FK_vocabulary_meaning_vocabulary_book_book_id'
                  AND conrelid = 'vocabulary_meaning'::regclass
            ) THEN
                ALTER TABLE vocabulary_meaning
                    ADD CONSTRAINT "FK_vocabulary_meaning_vocabulary_book_book_id"
                    FOREIGN KEY (book_id) REFERENCES vocabulary_book(id)
                    ON DELETE RESTRICT NOT VALID;
            END IF;

            IF NOT EXISTS (
                SELECT 1
                FROM vocabulary_meaning AS m
                LEFT JOIN vocabulary_book AS b ON b.id = m.book_id
                WHERE m.book_id IS NULL OR b.id IS NULL
            ) THEN
                ALTER TABLE vocabulary_meaning
                    VALIDATE CONSTRAINT "CK_vocabulary_meaning_book_id_required";
                ALTER TABLE vocabulary_meaning
                    VALIDATE CONSTRAINT "FK_vocabulary_meaning_vocabulary_book_book_id";
                ALTER TABLE vocabulary_meaning
                    ALTER COLUMN book_id SET NOT NULL;
            END IF;
        END
        $$;

        DROP INDEX IF EXISTS "IX_vocabulary_meaning_book_id";
        CREATE INDEX IF NOT EXISTS "IX_vocabulary_meaning_book_id_vocabulary_id"
            ON vocabulary_meaning (book_id, vocabulary_id);
        """;

    private static string? GetTableCreationSql(string tableName)
    {
        return tableName switch
        {
            "vocabulary" => @"
                CREATE TABLE IF NOT EXISTS vocabulary (
                    id character varying(36) NOT NULL,
                    word character varying(255) NOT NULL,
                    phonetic character varying(100) NULL,
                    created_at timestamp with time zone NOT NULL,
                    updated_at timestamp with time zone NOT NULL,
                    CONSTRAINT PK_vocabulary PRIMARY KEY (id)
                );
                CREATE UNIQUE INDEX IF NOT EXISTS IX_vocabulary_word ON vocabulary (word);",

            "vocabulary_book" => @"
                CREATE TABLE IF NOT EXISTS vocabulary_book (
                    id character varying(36) NOT NULL,
                    book_name character varying(255) NOT NULL,
                    description text NULL,
                    publisher character varying(255) NULL,
                    education_level character varying(100) NULL,
                    grade character varying(50) NULL,
                    category character varying(50) NULL,
                    display_order integer NOT NULL,
                    status boolean NOT NULL,
                    icon_url text NULL,
                    created_at timestamp with time zone NOT NULL,
                    updated_at timestamp with time zone NOT NULL,
                    CONSTRAINT PK_vocabulary_book PRIMARY KEY (id)
                );",

            "vocabulary_meaning" => @"
                CREATE TABLE IF NOT EXISTS vocabulary_meaning (
                    id character varying(36) NOT NULL,
                    vocabulary_id character varying(36) NOT NULL,
                    book_id character varying(36) NOT NULL,
                    part_of_speech character varying(50) NULL,
                    meaning text NOT NULL,
                    example text NULL,
                    created_at timestamp with time zone NOT NULL,
                    updated_at timestamp with time zone NOT NULL,
                    CONSTRAINT PK_vocabulary_meaning PRIMARY KEY (id),
                    CONSTRAINT FK_vocabulary_meaning_vocabulary_vocabulary_id 
                        FOREIGN KEY (vocabulary_id) REFERENCES vocabulary(id) ON DELETE CASCADE,
                    CONSTRAINT FK_vocabulary_meaning_vocabulary_book_book_id
                        FOREIGN KEY (book_id) REFERENCES vocabulary_book(id) ON DELETE RESTRICT
                );
                CREATE INDEX IF NOT EXISTS IX_vocabulary_meaning_vocabulary_id ON vocabulary_meaning (vocabulary_id);
                CREATE INDEX IF NOT EXISTS IX_vocabulary_meaning_book_id_vocabulary_id
                    ON vocabulary_meaning (book_id, vocabulary_id);",

            _ => null
        };
    }
}
