using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lexarbor.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddMeaningPhoneticEpochs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "meaning_phonetic_epoch",
                table: "audio_upload_operation",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "meaning_phonetic_epoch",
                columns: table => new
                {
                    meaning_id = table.Column<string>(type: "TEXT", nullable: false),
                    locale = table.Column<string>(type: "TEXT", nullable: false),
                    word_id = table.Column<string>(type: "TEXT", nullable: false),
                    epoch = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meaning_phonetic_epoch", x => new { x.meaning_id, x.locale });
                    table.CheckConstraint("CK_meaning_phonetic_epoch", "epoch > 0 AND locale IN ('en-GB','en-US')");
                    table.ForeignKey(
                        name: "FK_meaning_phonetic_epoch_vocabulary_meaning_meaning_id_word_id",
                        columns: x => new { x.meaning_id, x.word_id },
                        principalTable: "vocabulary_meaning",
                        principalColumns: new[] { "id", "vocabulary_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_meaning_phonetic_epoch_meaning_id_word_id",
                table: "meaning_phonetic_epoch",
                columns: new[] { "meaning_id", "word_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "meaning_phonetic_epoch");

            migrationBuilder.DropColumn(
                name: "meaning_phonetic_epoch",
                table: "audio_upload_operation");
        }
    }
}
