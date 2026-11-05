using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lexarbor.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddVocabularyRelations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "vocabulary_relation",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    source_word_id = table.Column<string>(type: "TEXT", nullable: false),
                    target_word_id = table.Column<string>(type: "TEXT", nullable: false),
                    type = table.Column<string>(type: "TEXT", nullable: false),
                    usage_note = table.Column<string>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "needs_review"),
                    review_reason = table.Column<string>(type: "TEXT", nullable: true, defaultValue: "new_relation"),
                    origin_kind = table.Column<string>(type: "TEXT", nullable: false),
                    origin_reference = table.Column<string>(type: "TEXT", nullable: true),
                    version = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 1L),
                    created_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    last_confirmed_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vocabulary_relation", x => x.id);
                    table.CheckConstraint("CK_vocabulary_relation_confirmation", "(status = 'confirmed' AND review_reason IS NULL AND last_confirmed_at_unix_ms IS NOT NULL) OR (status = 'needs_review' AND review_reason IS NOT NULL)");
                    table.CheckConstraint("CK_vocabulary_relation_id", "length(id) BETWEEN 1 AND 64 AND id = trim(id)");
                    table.CheckConstraint("CK_vocabulary_relation_origin", "origin_kind IN ('manual', 'import', 'generated')");
                    table.CheckConstraint("CK_vocabulary_relation_origin_reference", "origin_reference IS NULL OR (length(origin_reference) BETWEEN 1 AND 200 AND origin_reference = trim(origin_reference))");
                    table.CheckConstraint("CK_vocabulary_relation_review_reason", "review_reason IS NULL OR review_reason IN ('new_relation', 'word_changed', 'relation_changed')");
                    table.CheckConstraint("CK_vocabulary_relation_status", "status IN ('confirmed', 'needs_review')");
                    table.CheckConstraint("CK_vocabulary_relation_times", "typeof(created_at_unix_ms) = 'integer' AND created_at_unix_ms >= 0 AND typeof(updated_at_unix_ms) = 'integer' AND updated_at_unix_ms >= created_at_unix_ms AND (last_confirmed_at_unix_ms IS NULL OR (typeof(last_confirmed_at_unix_ms) = 'integer' AND last_confirmed_at_unix_ms >= created_at_unix_ms AND last_confirmed_at_unix_ms <= updated_at_unix_ms))");
                    table.CheckConstraint("CK_vocabulary_relation_type", "type IN ('past_tense', 'past_participle', 'ing_form', 'third_person_singular', 'plural', 'comparative', 'superlative', 'derived_noun', 'derived_verb', 'derived_adjective', 'derived_adverb')");
                    table.CheckConstraint("CK_vocabulary_relation_usage_note", "usage_note IS NULL OR (length(usage_note) BETWEEN 1 AND 2000 AND usage_note = trim(usage_note))");
                    table.CheckConstraint("CK_vocabulary_relation_version", "typeof(version) = 'integer' AND version >= 1");
                    table.ForeignKey(
                        name: "FK_vocabulary_relation_source",
                        column: x => x.source_word_id,
                        principalTable: "vocabulary",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict,
                        onUpdate: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_vocabulary_relation_target",
                        column: x => x.target_word_id,
                        principalTable: "vocabulary",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict,
                        onUpdate: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_relation_needs_review",
                table: "vocabulary_relation",
                columns: new[] { "updated_at_unix_ms", "id" },
                filter: "status = 'needs_review'");

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_relation_source_type_status",
                table: "vocabulary_relation",
                columns: new[] { "source_word_id", "type", "status", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_relation_target_type_status",
                table: "vocabulary_relation",
                columns: new[] { "target_word_id", "type", "status", "id" });

            migrationBuilder.CreateIndex(
                name: "UX_vocabulary_relation_identity",
                table: "vocabulary_relation",
                columns: new[] { "source_word_id", "target_word_id", "type" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "vocabulary_relation");
        }
    }
}
