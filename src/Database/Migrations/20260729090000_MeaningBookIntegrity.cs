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
            migrationBuilder.Sql(DatabaseInitializer.BuildMeaningBookIntegritySql());
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
