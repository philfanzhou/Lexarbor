using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lexarbor.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddVocabularyBookUnits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The composite foreign keys below reference (id, book_id) parent
            // keys. SQLite accepts a unique index as a parent key, and only an
            // index can be added to vocabulary_meaning, a table that already
            // exists: SQLite has no ALTER TABLE ... ADD CONSTRAINT. The model
            // still declares these as alternate keys, and the unique indexes
            // IX_vocabulary_meaning_id_book_id / IX_vocabulary_book_unit_id_book_id
            // are what realize them in the database.
            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_meaning_id_book_id",
                table: "vocabulary_meaning",
                columns: new[] { "id", "book_id" },
                unique: true);

            migrationBuilder.CreateTable(
                name: "vocabulary_book_unit",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    book_id = table.Column<string>(type: "TEXT", nullable: false),
                    number = table.Column<int>(type: "INTEGER", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vocabulary_book_unit", x => x.id);
                    table.ForeignKey(
                        name: "FK_vocabulary_book_unit_vocabulary_book_book_id",
                        column: x => x.book_id,
                        principalTable: "vocabulary_book",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "vocabulary_meaning_unit",
                columns: table => new
                {
                    unit_id = table.Column<string>(type: "TEXT", nullable: false),
                    meaning_id = table.Column<string>(type: "TEXT", nullable: false),
                    book_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vocabulary_meaning_unit", x => new { x.unit_id, x.meaning_id });
                    table.ForeignKey(
                        name: "FK_vocabulary_meaning_unit_vocabulary_book_unit_unit_id_book_id",
                        columns: x => new { x.unit_id, x.book_id },
                        principalTable: "vocabulary_book_unit",
                        principalColumns: new[] { "id", "book_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_vocabulary_meaning_unit_vocabulary_meaning_meaning_id_book_id",
                        columns: x => new { x.meaning_id, x.book_id },
                        principalTable: "vocabulary_meaning",
                        principalColumns: new[] { "id", "book_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_book_unit_book_id_number",
                table: "vocabulary_book_unit",
                columns: new[] { "book_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_book_unit_id_book_id",
                table: "vocabulary_book_unit",
                columns: new[] { "id", "book_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_meaning_unit_meaning_id_book_id",
                table: "vocabulary_meaning_unit",
                columns: new[] { "meaning_id", "book_id" });

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_meaning_unit_unit_id_book_id",
                table: "vocabulary_meaning_unit",
                columns: new[] { "unit_id", "book_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "vocabulary_meaning_unit");

            migrationBuilder.DropTable(
                name: "vocabulary_book_unit");

            migrationBuilder.DropIndex(
                name: "IX_vocabulary_meaning_id_book_id",
                table: "vocabulary_meaning");
        }
    }
}
