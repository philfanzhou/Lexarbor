using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lexarbor.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddMeaningUnitEntryKinds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite has no ALTER TABLE for a primary key, so the generated
            // DropPrimaryKey/AddPrimaryKey pair cannot run. The whole change —
            // the entry_kind column with its empty-string default, the wider
            // (unit_id, meaning_id, section, entry_kind) key, and the CHECK
            // constraint — arrives through a table rebuild: create the new
            // shape, copy every existing row in as an unclassified assignment
            // (the empty-string sentinel, never NULL, because SQLite treats
            // NULLs in a composite primary key as mutually unequal and a NULL
            // kind could not keep a repeated unclassified assignment from
            // being stored twice), drop the old table, rename, and recreate
            // the two membership indexes the drop took with it. The two
            // indexes and foreign keys mirror AddMeaningUnitSections exactly;
            // no existing row changes value.
            migrationBuilder.Sql("""
                CREATE TABLE "vocabulary_meaning_unit__kinds" (
                    "unit_id" TEXT NOT NULL,
                    "meaning_id" TEXT NOT NULL,
                    "book_id" TEXT NOT NULL,
                    "section" TEXT NOT NULL DEFAULT '',
                    "entry_kind" TEXT NOT NULL DEFAULT '',
                    CONSTRAINT "PK_vocabulary_meaning_unit" PRIMARY KEY ("unit_id", "meaning_id", "section", "entry_kind"),
                    CONSTRAINT "CK_vocabulary_meaning_unit_section" CHECK (section IN ('', 'A', 'B')),
                    CONSTRAINT "CK_vocabulary_meaning_unit_entry_kind" CHECK (entry_kind IN ('', 'word', 'phrase')),
                    CONSTRAINT "FK_vocabulary_meaning_unit_vocabulary_book_unit_unit_id_book_id" FOREIGN KEY ("unit_id", "book_id") REFERENCES "vocabulary_book_unit" ("id", "book_id") ON DELETE CASCADE,
                    CONSTRAINT "FK_vocabulary_meaning_unit_vocabulary_meaning_meaning_id_book_id" FOREIGN KEY ("meaning_id", "book_id") REFERENCES "vocabulary_meaning" ("id", "book_id") ON DELETE CASCADE
                );
                INSERT INTO "vocabulary_meaning_unit__kinds" ("unit_id", "meaning_id", "book_id", "section", "entry_kind")
                SELECT "unit_id", "meaning_id", "book_id", "section", '' FROM "vocabulary_meaning_unit";
                DROP TABLE "vocabulary_meaning_unit";
                ALTER TABLE "vocabulary_meaning_unit__kinds" RENAME TO "vocabulary_meaning_unit";
                CREATE INDEX "IX_vocabulary_meaning_unit_meaning_id_book_id" ON "vocabulary_meaning_unit" ("meaning_id", "book_id");
                CREATE INDEX "IX_vocabulary_meaning_unit_unit_id_book_id" ON "vocabulary_meaning_unit" ("unit_id", "book_id");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The reverse rebuild cannot keep both kinds of one place: the old
            // key allows only one row per (unit_id, meaning_id, section), so
            // multi-kind data is merged first — one row per triple survives
            // with its section intact (the kind split is lost, the assignment
            // is not), and book_id is a function of the pair through both
            // foreign keys, so the group's value is the same whichever row
            // SQLite keeps — and only then is the table rebuilt without the
            // entry_kind column.
            migrationBuilder.Sql("""
                CREATE TABLE "vocabulary_meaning_unit__nokinds" (
                    "unit_id" TEXT NOT NULL,
                    "meaning_id" TEXT NOT NULL,
                    "book_id" TEXT NOT NULL,
                    "section" TEXT NOT NULL DEFAULT '',
                    CONSTRAINT "PK_vocabulary_meaning_unit" PRIMARY KEY ("unit_id", "meaning_id", "section"),
                    CONSTRAINT "CK_vocabulary_meaning_unit_section" CHECK (section IN ('', 'A', 'B')),
                    CONSTRAINT "FK_vocabulary_meaning_unit_vocabulary_book_unit_unit_id_book_id" FOREIGN KEY ("unit_id", "book_id") REFERENCES "vocabulary_book_unit" ("id", "book_id") ON DELETE CASCADE,
                    CONSTRAINT "FK_vocabulary_meaning_unit_vocabulary_meaning_meaning_id_book_id" FOREIGN KEY ("meaning_id", "book_id") REFERENCES "vocabulary_meaning" ("id", "book_id") ON DELETE CASCADE
                );
                INSERT INTO "vocabulary_meaning_unit__nokinds" ("unit_id", "meaning_id", "book_id", "section")
                SELECT "unit_id", "meaning_id", "book_id", "section" FROM "vocabulary_meaning_unit"
                GROUP BY "unit_id", "meaning_id", "section";
                DROP TABLE "vocabulary_meaning_unit";
                ALTER TABLE "vocabulary_meaning_unit__nokinds" RENAME TO "vocabulary_meaning_unit";
                CREATE INDEX "IX_vocabulary_meaning_unit_meaning_id_book_id" ON "vocabulary_meaning_unit" ("meaning_id", "book_id");
                CREATE INDEX "IX_vocabulary_meaning_unit_unit_id_book_id" ON "vocabulary_meaning_unit" ("unit_id", "book_id");
                """);
        }
    }
}
