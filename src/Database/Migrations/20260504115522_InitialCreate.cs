using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ruoyu.Study.Vocabulary.Database.Migrations
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
                    id = table.Column<string>(type: "text", nullable: false),
                    word = table.Column<string>(type: "text", nullable: false),
                    phonetic = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vocabulary", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "vocabulary_book",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    book_name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    publisher = table.Column<string>(type: "text", nullable: true),
                    education_level = table.Column<string>(type: "text", nullable: true),
                    grade = table.Column<string>(type: "text", nullable: true),
                    category = table.Column<string>(type: "text", nullable: true),
                    display_order = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<bool>(type: "boolean", nullable: false),
                    icon_url = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vocabulary_book", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "vocabulary_meaning",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    vocabulary_id = table.Column<string>(type: "text", nullable: false),
                    book_id = table.Column<string>(type: "text", nullable: true),
                    part_of_speech = table.Column<string>(type: "text", nullable: true),
                    meaning = table.Column<string>(type: "text", nullable: false),
                    example = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vocabulary_meaning", x => x.id);
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
                name: "IX_vocabulary_meaning_book_id",
                table: "vocabulary_meaning",
                column: "book_id");

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_meaning_vocabulary_id",
                table: "vocabulary_meaning",
                column: "vocabulary_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "vocabulary_book");

            migrationBuilder.DropTable(
                name: "vocabulary_meaning");

            migrationBuilder.DropTable(
                name: "vocabulary");
        }
    }
}
