using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lexarbor.Database.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "vocabulary",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    word = table.Column<string>(type: "TEXT", nullable: false),
                    phonetic_uk = table.Column<string>(type: "TEXT", nullable: true),
                    phonetic_us = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vocabulary", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "vocabulary_book",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    book_name = table.Column<string>(type: "TEXT", nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    publisher = table.Column<string>(type: "TEXT", nullable: true),
                    education_level = table.Column<string>(type: "TEXT", nullable: true),
                    grade = table.Column<string>(type: "TEXT", nullable: true),
                    category = table.Column<string>(type: "TEXT", nullable: true),
                    display_order = table.Column<int>(type: "INTEGER", nullable: false),
                    status = table.Column<bool>(type: "INTEGER", nullable: false),
                    icon_url = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vocabulary_book", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "vocabulary_meaning",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    vocabulary_id = table.Column<string>(type: "TEXT", nullable: false),
                    book_id = table.Column<string>(type: "TEXT", nullable: false),
                    part_of_speech = table.Column<string>(type: "TEXT", nullable: true),
                    meaning = table.Column<string>(type: "TEXT", nullable: false),
                    normalized_part_of_speech = table.Column<string>(type: "TEXT", nullable: false, computedColumnSql: "lower(trim(coalesce(part_of_speech, '')))", stored: true),
                    normalized_meaning = table.Column<string>(type: "TEXT", nullable: false, computedColumnSql: "trim(meaning)", stored: true),
                    example = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vocabulary_meaning", x => x.id);
                    table.ForeignKey(
                        name: "FK_vocabulary_meaning_vocabulary_book_book_id",
                        column: x => x.book_id,
                        principalTable: "vocabulary_book",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_vocabulary_meaning_vocabulary_vocabulary_id",
                        column: x => x.vocabulary_id,
                        principalTable: "vocabulary",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_word",
                table: "vocabulary",
                column: "word",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_meaning_book_id_vocabulary_id",
                table: "vocabulary_meaning",
                columns: new[] { "book_id", "vocabulary_id" });

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_meaning_vocabulary_id",
                table: "vocabulary_meaning",
                column: "vocabulary_id");

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_meaning_vocabulary_id_book_id_normalized_part_of_speech_normalized_meaning",
                table: "vocabulary_meaning",
                columns: new[] { "vocabulary_id", "book_id", "normalized_part_of_speech", "normalized_meaning" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "vocabulary_meaning");

            migrationBuilder.DropTable(
                name: "vocabulary_book");

            migrationBuilder.DropTable(
                name: "vocabulary");
        }
    }
}
