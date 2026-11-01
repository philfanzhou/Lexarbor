using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lexarbor.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddAudioInvalidations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audio_invalidation",
                columns: table => new
                {
                    asset_id = table.Column<string>(type: "TEXT", nullable: false),
                    bucket = table.Column<string>(type: "TEXT", nullable: false),
                    object_key = table.Column<string>(type: "TEXT", nullable: false),
                    word_id = table.Column<string>(type: "TEXT", nullable: false),
                    meaning_id = table.Column<string>(type: "TEXT", nullable: false),
                    locale = table.Column<string>(type: "TEXT", nullable: false),
                    authorization_epoch = table.Column<long>(type: "INTEGER", nullable: false),
                    reason = table.Column<string>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    created_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    lease_owner = table.Column<string>(type: "TEXT", nullable: true),
                    lease_fence = table.Column<long>(type: "INTEGER", nullable: false),
                    lease_expires_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: true),
                    attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    next_attempt_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    delete_started_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: true),
                    observed_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: true),
                    error_code = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audio_invalidation", x => x.asset_id);
                    table.CheckConstraint("CK_audio_invalidation_authority", "length(word_id) > 0 AND length(meaning_id) > 0 AND authorization_epoch > 0 AND reason = 'specific_phonetic_changed' AND locale IN ('en-GB','en-US')");
                    table.CheckConstraint("CK_audio_invalidation_lease", "(state = 'Claimed' AND lease_owner IS NOT NULL AND lease_expires_at_unix_ms IS NOT NULL AND lease_fence > 0 AND delete_started_at_unix_ms IS NOT NULL) OR (state != 'Claimed' AND lease_owner IS NULL AND lease_expires_at_unix_ms IS NULL)");
                    table.CheckConstraint("CK_audio_invalidation_location", "length(bucket) > 0 AND length(object_key) > 0");
                    table.CheckConstraint("CK_audio_invalidation_observed", "state != 'Observed' OR (observed_at_unix_ms IS NOT NULL AND delete_started_at_unix_ms IS NOT NULL)");
                    table.CheckConstraint("CK_audio_invalidation_state", "state IN ('Waiting','Claimed','Observed') AND attempts >= 0 AND lease_fence >= 0");
                    table.CheckConstraint("CK_audio_invalidation_times", "created_at_unix_ms >= 0 AND updated_at_unix_ms >= created_at_unix_ms AND next_attempt_at_unix_ms >= 0 AND (delete_started_at_unix_ms IS NULL OR delete_started_at_unix_ms >= created_at_unix_ms) AND (observed_at_unix_ms IS NULL OR observed_at_unix_ms >= delete_started_at_unix_ms)");
                    table.ForeignKey(
                        name: "FK_audio_invalidation_audio_asset_asset_id",
                        column: x => x.asset_id,
                        principalTable: "audio_asset",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audio_invalidation_state_next_attempt_at_unix_ms",
                table: "audio_invalidation",
                columns: new[] { "state", "next_attempt_at_unix_ms" });

            migrationBuilder.Sql("""
                CREATE TRIGGER audio_invalidation_authorize BEFORE INSERT ON audio_invalidation
                WHEN NEW.state != 'Waiting' OR NEW.delete_started_at_unix_ms IS NOT NULL OR NOT EXISTS (
                    SELECT 1 FROM vocabulary_audio_binding b JOIN audio_asset a ON a.id = b.asset_id
                    WHERE b.word_id = NEW.word_id AND b.meaning_id = NEW.meaning_id AND b.locale = NEW.locale
                        AND a.id = NEW.asset_id AND a.bucket = NEW.bucket AND a.object_key = NEW.object_key
                        AND a.first_published_at_unix_ms IS NOT NULL)
                BEGIN SELECT RAISE(ABORT, 'Audio invalidation authority is invalid.'); END;
                CREATE TRIGGER audio_invalidation_immutable BEFORE UPDATE ON audio_invalidation
                WHEN NEW.asset_id IS NOT OLD.asset_id OR NEW.bucket IS NOT OLD.bucket OR NEW.object_key IS NOT OLD.object_key
                    OR NEW.word_id IS NOT OLD.word_id OR NEW.meaning_id IS NOT OLD.meaning_id OR NEW.locale IS NOT OLD.locale
                    OR NEW.authorization_epoch IS NOT OLD.authorization_epoch OR NEW.reason IS NOT OLD.reason
                    OR NEW.created_at_unix_ms IS NOT OLD.created_at_unix_ms
                    OR (OLD.delete_started_at_unix_ms IS NOT NULL AND NEW.delete_started_at_unix_ms IS NOT OLD.delete_started_at_unix_ms)
                BEGIN SELECT RAISE(ABORT, 'Audio invalidation authority is permanent.'); END;
                CREATE TRIGGER audio_invalidation_claim BEFORE UPDATE OF delete_started_at_unix_ms ON audio_invalidation
                WHEN OLD.delete_started_at_unix_ms IS NULL AND NEW.delete_started_at_unix_ms IS NOT NULL
                    AND (EXISTS (SELECT 1 FROM vocabulary_audio_binding WHERE asset_id = NEW.asset_id)
                        OR NOT EXISTS (SELECT 1 FROM audio_asset WHERE id = NEW.asset_id AND bucket = NEW.bucket
                            AND object_key = NEW.object_key AND first_published_at_unix_ms IS NOT NULL))
                BEGIN SELECT RAISE(ABORT, 'Referenced audio cannot be deleted.'); END;
                CREATE TRIGGER audio_invalidation_retain BEFORE DELETE ON audio_invalidation
                BEGIN SELECT RAISE(ABORT, 'Audio invalidation tombstones are permanent.'); END;
                CREATE TRIGGER audio_invalidation_asset_location BEFORE UPDATE OF bucket, object_key ON audio_asset
                WHEN EXISTS (SELECT 1 FROM audio_invalidation WHERE asset_id = OLD.id)
                    AND (NEW.bucket IS NOT OLD.bucket OR NEW.object_key IS NOT OLD.object_key)
                BEGIN SELECT RAISE(ABORT, 'Authorized audio location is immutable.'); END;
                CREATE TRIGGER audio_binding_invalidation_insert BEFORE INSERT ON vocabulary_audio_binding
                WHEN EXISTS (SELECT 1 FROM audio_invalidation WHERE asset_id = NEW.asset_id AND delete_started_at_unix_ms IS NOT NULL)
                BEGIN SELECT RAISE(ABORT, 'Deleted audio cannot be referenced.'); END;
                CREATE TRIGGER audio_binding_invalidation_update BEFORE UPDATE OF asset_id ON vocabulary_audio_binding
                WHEN EXISTS (SELECT 1 FROM audio_invalidation WHERE asset_id = NEW.asset_id AND delete_started_at_unix_ms IS NOT NULL)
                BEGIN SELECT RAISE(ABORT, 'Deleted audio cannot be referenced.'); END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER audio_binding_invalidation_update;
                DROP TRIGGER audio_binding_invalidation_insert;
                DROP TRIGGER audio_invalidation_asset_location;
                DROP TRIGGER audio_invalidation_retain;
                DROP TRIGGER audio_invalidation_claim;
                DROP TRIGGER audio_invalidation_immutable;
                DROP TRIGGER audio_invalidation_authorize;
                """);
            migrationBuilder.DropTable(
                name: "audio_invalidation");
        }
    }
}
