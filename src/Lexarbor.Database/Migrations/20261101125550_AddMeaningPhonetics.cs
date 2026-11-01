using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lexarbor.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddMeaningPhonetics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "phonetic_uk",
                table: "vocabulary_meaning",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "phonetic_us",
                table: "vocabulary_meaning",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // SQLite is the only supported database. Native drops preserve
            // the original table/index definitions; EF's rebuild would also
            // materialize redundant alternate-key constraints from the model.
            migrationBuilder.Sql("ALTER TABLE vocabulary_meaning DROP COLUMN phonetic_uk;");
            migrationBuilder.Sql("ALTER TABLE vocabulary_meaning DROP COLUMN phonetic_us;");
        }
    }
}
