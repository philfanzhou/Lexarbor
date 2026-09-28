using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lexarbor.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddMeaningUnitSections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite has no ALTER TABLE for a primary key, so the generated
            // DropPrimaryKey/AddPrimaryKey pair cannot run. The whole change —
            // the section column with its empty-string default, the wider
            // (unit_id, meaning_id, section) key, and the CHECK constraint —
            // arrives through a table rebuild: create the new shape, copy every
            // existing row in as an unsectioned assignment (the empty-string
            // sentinel, never NULL, because SQLite treats NULLs in a composite
            // primary key as mutually unequal and a NULL section could not keep
            // a repeated unsectioned assignment from being stored twice), drop
            // the old table, rename, and recreate the two membership indexes
            // the drop took with it. The two indexes and foreign keys mirror
            // AddVocabularyBookUnits exactly; no existing row changes value.
            migrationBuilder.Sql("""
                CREATE TABLE "vocabulary_meaning_unit__sections" (
                    "unit_id" TEXT NOT NULL,
                    "meaning_id" TEXT NOT NULL,
                    "book_id" TEXT NOT NULL,
                    "section" TEXT NOT NULL DEFAULT '',
                    CONSTRAINT "PK_vocabulary_meaning_unit" PRIMARY KEY ("unit_id", "meaning_id", "section"),
                    CONSTRAINT "CK_vocabulary_meaning_unit_section" CHECK (section IN ('', 'A', 'B')),
                    CONSTRAINT "FK_vocabulary_meaning_unit_vocabulary_book_unit_unit_id_book_id" FOREIGN KEY ("unit_id", "book_id") REFERENCES "vocabulary_book_unit" ("id", "book_id") ON DELETE CASCADE,
                    CONSTRAINT "FK_vocabulary_meaning_unit_vocabulary_meaning_meaning_id_book_id" FOREIGN KEY ("meaning_id", "book_id") REFERENCES "vocabulary_meaning" ("id", "book_id") ON DELETE CASCADE
                );
                INSERT INTO "vocabulary_meaning_unit__sections" ("unit_id", "meaning_id", "book_id", "section")
                SELECT "unit_id", "meaning_id", "book_id", '' FROM "vocabulary_meaning_unit";
                DROP TABLE "vocabulary_meaning_unit";
                ALTER TABLE "vocabulary_meaning_unit__sections" RENAME TO "vocabulary_meaning_unit";
                CREATE INDEX "IX_vocabulary_meaning_unit_meaning_id_book_id" ON "vocabulary_meaning_unit" ("meaning_id", "book_id");
                CREATE INDEX "IX_vocabulary_meaning_unit_unit_id_book_id" ON "vocabulary_meaning_unit" ("unit_id", "book_id");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The reverse rebuild cannot keep both positions of one meaning:
            // the old key allows only one row per (unit_id, meaning_id), so
            // multi-position data is merged first — one row per pair survives,
            // and book_id is a function of the pair through both foreign keys,
            // so the group's value is the same whichever row SQLite keeps —
            // and only then is the table rebuilt without the section column.
            migrationBuilder.Sql("""
                CREATE TABLE "vocabulary_meaning_unit__nosections" (
                    "unit_id" TEXT NOT NULL,
                    "meaning_id" TEXT NOT NULL,
                    "book_id" TEXT NOT NULL,
                    CONSTRAINT "PK_vocabulary_meaning_unit" PRIMARY KEY ("unit_id", "meaning_id"),
                    CONSTRAINT "FK_vocabulary_meaning_unit_vocabulary_book_unit_unit_id_book_id" FOREIGN KEY ("unit_id", "book_id") REFERENCES "vocabulary_book_unit" ("id", "book_id") ON DELETE CASCADE,
                    CONSTRAINT "FK_vocabulary_meaning_unit_vocabulary_meaning_meaning_id_book_id" FOREIGN KEY ("meaning_id", "book_id") REFERENCES "vocabulary_meaning" ("id", "book_id") ON DELETE CASCADE
                );
                INSERT INTO "vocabulary_meaning_unit__nosections" ("unit_id", "meaning_id", "book_id")
                SELECT "unit_id", "meaning_id", "book_id" FROM "vocabulary_meaning_unit"
                GROUP BY "unit_id", "meaning_id";
                DROP TABLE "vocabulary_meaning_unit";
                ALTER TABLE "vocabulary_meaning_unit__nosections" RENAME TO "vocabulary_meaning_unit";
                CREATE INDEX "IX_vocabulary_meaning_unit_meaning_id_book_id" ON "vocabulary_meaning_unit" ("meaning_id", "book_id");
                CREATE INDEX "IX_vocabulary_meaning_unit_unit_id_book_id" ON "vocabulary_meaning_unit" ("unit_id", "book_id");
                """);
        }
    }
}
