using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lexarbor.Database.LegacySqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddVocabularyRelationMeanings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "vocabulary_relation_meaning",
                columns: table => new
                {
                    relation_id = table.Column<string>(type: "TEXT", nullable: false),
                    source_meaning_id = table.Column<string>(type: "TEXT", nullable: false),
                    target_meaning_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vocabulary_relation_meaning", x => new { x.relation_id, x.source_meaning_id, x.target_meaning_id });
                    table.ForeignKey(
                        name: "FK_vocabulary_relation_meaning_vocabulary_meaning_source_meaning_id",
                        column: x => x.source_meaning_id,
                        principalTable: "vocabulary_meaning",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_vocabulary_relation_meaning_vocabulary_meaning_target_meaning_id",
                        column: x => x.target_meaning_id,
                        principalTable: "vocabulary_meaning",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_vocabulary_relation_meaning_vocabulary_relation_relation_id",
                        column: x => x.relation_id,
                        principalTable: "vocabulary_relation",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_relation_meaning_source_meaning_id",
                table: "vocabulary_relation_meaning",
                column: "source_meaning_id");

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_relation_meaning_target_meaning_id",
                table: "vocabulary_relation_meaning",
                column: "target_meaning_id");

            const string invalidateMeaning = """
                UPDATE vocabulary_relation
                SET version = version + 1,
                    updated_at_unix_ms = max(updated_at_unix_ms, CAST(unixepoch('subsec') * 1000 AS INTEGER))
                WHERE id IN (
                    SELECT relation_id FROM vocabulary_relation_meaning
                    WHERE source_meaning_id = OLD.id OR target_meaning_id = OLD.id
                );
                DELETE FROM vocabulary_relation_meaning
                WHERE source_meaning_id = OLD.id OR target_meaning_id = OLD.id;
                """;
            migrationBuilder.Sql($"""
                CREATE TRIGGER vocabulary_relation_meaning_invalidated
                AFTER UPDATE OF meaning, part_of_speech, example, vocabulary_id, book_id ON vocabulary_meaning
                WHEN NEW.normalized_meaning IS NOT OLD.normalized_meaning
                    OR NEW.normalized_part_of_speech IS NOT OLD.normalized_part_of_speech
                    OR trim(NEW.example) IS NOT trim(OLD.example)
                    OR NEW.vocabulary_id IS NOT OLD.vocabulary_id OR NEW.book_id IS NOT OLD.book_id
                BEGIN
                    {invalidateMeaning}
                END;

                CREATE TRIGGER vocabulary_relation_meaning_deleted BEFORE DELETE ON vocabulary_meaning
                BEGIN
                    {invalidateMeaning}
                END;

                CREATE TRIGGER vocabulary_relation_meaning_scope_changed
                AFTER UPDATE OF source_word_id, target_word_id, type, usage_note, status ON vocabulary_relation
                WHEN NEW.status <> 'confirmed' OR NEW.source_word_id IS NOT OLD.source_word_id
                    OR NEW.target_word_id IS NOT OLD.target_word_id OR NEW.type IS NOT OLD.type
                    OR NEW.usage_note IS NOT OLD.usage_note
                BEGIN
                    DELETE FROM vocabulary_relation_meaning WHERE relation_id = OLD.id;
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER vocabulary_relation_meaning_scope_changed;
                DROP TRIGGER vocabulary_relation_meaning_deleted;
                DROP TRIGGER vocabulary_relation_meaning_invalidated;
                """);
            migrationBuilder.DropTable(
                name: "vocabulary_relation_meaning");
        }
    }
}
