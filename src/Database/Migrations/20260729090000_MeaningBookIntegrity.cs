using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ruoyu.Study.Vocabulary.Database.Migrations
{
    /// <inheritdoc />
    public partial class MeaningBookIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
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
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE vocabulary_meaning
                    DROP CONSTRAINT IF EXISTS "FK_vocabulary_meaning_vocabulary_book_book_id";
                ALTER TABLE vocabulary_meaning
                    DROP CONSTRAINT IF EXISTS "CK_vocabulary_meaning_book_id_required";
                ALTER TABLE vocabulary_meaning
                    ALTER COLUMN book_id DROP NOT NULL;

                DROP INDEX IF EXISTS "IX_vocabulary_meaning_book_id_vocabulary_id";
                CREATE INDEX IF NOT EXISTS "IX_vocabulary_meaning_book_id"
                    ON vocabulary_meaning (book_id);
                """);
        }
    }
}
