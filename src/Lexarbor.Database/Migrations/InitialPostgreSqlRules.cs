namespace Lexarbor.Database.Migrations;

/// <summary>
/// The rules the SQLite schema enforced with triggers, and the column defaults
/// its migrations added outside the EF model, carried over one for one into
/// the PostgreSQL baseline.
/// </summary>
/// <remarks>
/// A refusal raises SQLSTATE 23000 (integrity constraint violation), the class
/// the unit of work reports as a conflict, as SQLite reported a trigger
/// <c>RAISE(ABORT)</c> as SQLITE_CONSTRAINT. Conditions that read other tables
/// live in the function bodies because a PostgreSQL trigger's <c>WHEN</c>
/// clause cannot hold a subquery. Kept apart from the generated migration so
/// that regenerating the baseline cannot drop them silently; the migration
/// calls <see cref="Up"/> and <see cref="Down"/>.
/// </remarks>
internal static class InitialPostgreSqlRules
{
    internal const string Up = """
        -- Column defaults the SQLite migrations added with the columns; the EF
        -- model always writes these values, but rows written by other tools
        -- (and copied rows) rely on them.
        ALTER TABLE enrichment_discovery ALTER COLUMN initial_completed SET DEFAULT false;
        ALTER TABLE enrichment_discovery ALTER COLUMN initial_scanned SET DEFAULT 0;
        ALTER TABLE enrichment_discovery ALTER COLUMN initial_total SET DEFAULT 0;
        ALTER TABLE enrichment_round ALTER COLUMN fields_not_required SET DEFAULT false;
        ALTER TABLE enrichment_round ALTER COLUMN force_single SET DEFAULT false;

        CREATE FUNCTION lexarbor_now_unix_ms() RETURNS bigint LANGUAGE sql STABLE AS $$
            SELECT floor(extract(epoch FROM statement_timestamp()) * 1000)::bigint
        $$;

        CREATE FUNCTION lexarbor_audio_binding_invalidation() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF EXISTS (SELECT 1 FROM audio_invalidation
                       WHERE asset_id = NEW.asset_id AND delete_started_at_unix_ms IS NOT NULL) THEN
                RAISE EXCEPTION 'Deleted audio cannot be referenced.' USING ERRCODE = '23000';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER audio_binding_invalidation_insert BEFORE INSERT ON vocabulary_audio_binding
            FOR EACH ROW EXECUTE FUNCTION lexarbor_audio_binding_invalidation();
        CREATE TRIGGER audio_binding_invalidation_update BEFORE UPDATE OF asset_id ON vocabulary_audio_binding
            FOR EACH ROW EXECUTE FUNCTION lexarbor_audio_binding_invalidation();

        CREATE FUNCTION lexarbor_audio_invalidation_asset_location() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF (NEW.bucket IS DISTINCT FROM OLD.bucket OR NEW.object_key IS DISTINCT FROM OLD.object_key)
               AND EXISTS (SELECT 1 FROM audio_invalidation WHERE asset_id = OLD.id) THEN
                RAISE EXCEPTION 'Authorized audio location is immutable.' USING ERRCODE = '23000';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER audio_invalidation_asset_location BEFORE UPDATE OF bucket, object_key ON audio_asset
            FOR EACH ROW EXECUTE FUNCTION lexarbor_audio_invalidation_asset_location();

        CREATE FUNCTION lexarbor_audio_invalidation_authorize() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF NEW.state <> 'Waiting' OR NEW.delete_started_at_unix_ms IS NOT NULL OR NOT EXISTS (
                SELECT 1 FROM vocabulary_audio_binding b JOIN audio_asset a ON a.id = b.asset_id
                WHERE b.word_id = NEW.word_id AND b.meaning_id = NEW.meaning_id AND b.locale = NEW.locale
                    AND a.id = NEW.asset_id AND a.bucket = NEW.bucket AND a.object_key = NEW.object_key
                    AND a.first_published_at_unix_ms IS NOT NULL) THEN
                RAISE EXCEPTION 'Audio invalidation authority is invalid.' USING ERRCODE = '23000';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER audio_invalidation_authorize BEFORE INSERT ON audio_invalidation
            FOR EACH ROW EXECUTE FUNCTION lexarbor_audio_invalidation_authorize();

        CREATE FUNCTION lexarbor_audio_invalidation_claim() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF OLD.delete_started_at_unix_ms IS NULL AND NEW.delete_started_at_unix_ms IS NOT NULL
               AND (EXISTS (SELECT 1 FROM vocabulary_audio_binding WHERE asset_id = NEW.asset_id)
                    OR NOT EXISTS (SELECT 1 FROM audio_asset WHERE id = NEW.asset_id AND bucket = NEW.bucket
                        AND object_key = NEW.object_key AND first_published_at_unix_ms IS NOT NULL)) THEN
                RAISE EXCEPTION 'Referenced audio cannot be deleted.' USING ERRCODE = '23000';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER audio_invalidation_claim BEFORE UPDATE OF delete_started_at_unix_ms ON audio_invalidation
            FOR EACH ROW EXECUTE FUNCTION lexarbor_audio_invalidation_claim();

        CREATE FUNCTION lexarbor_audio_invalidation_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'Audio invalidation authority is permanent.' USING ERRCODE = '23000';
        END $$;
        CREATE TRIGGER audio_invalidation_immutable BEFORE UPDATE ON audio_invalidation
            FOR EACH ROW
            WHEN (NEW.asset_id IS DISTINCT FROM OLD.asset_id OR NEW.bucket IS DISTINCT FROM OLD.bucket
                OR NEW.object_key IS DISTINCT FROM OLD.object_key OR NEW.word_id IS DISTINCT FROM OLD.word_id
                OR NEW.meaning_id IS DISTINCT FROM OLD.meaning_id OR NEW.locale IS DISTINCT FROM OLD.locale
                OR NEW.authorization_epoch IS DISTINCT FROM OLD.authorization_epoch OR NEW.reason IS DISTINCT FROM OLD.reason
                OR NEW.created_at_unix_ms IS DISTINCT FROM OLD.created_at_unix_ms
                OR (OLD.delete_started_at_unix_ms IS NOT NULL
                    AND NEW.delete_started_at_unix_ms IS DISTINCT FROM OLD.delete_started_at_unix_ms))
            EXECUTE FUNCTION lexarbor_audio_invalidation_immutable();

        CREATE FUNCTION lexarbor_audio_invalidation_retain() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'Audio invalidation tombstones are permanent.' USING ERRCODE = '23000';
        END $$;
        CREATE TRIGGER audio_invalidation_retain BEFORE DELETE ON audio_invalidation
            FOR EACH ROW EXECUTE FUNCTION lexarbor_audio_invalidation_retain();

        CREATE FUNCTION lexarbor_enrichment_word_deleted() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            UPDATE enrichment_target SET deleted = true, epoch = epoch + 1
            WHERE word_id = OLD.id AND NOT deleted;
            UPDATE enrichment_round SET state = 'Cancelled', lease_owner = NULL,
                lease_expires_at_unix_ms = NULL, updated_at_unix_ms = greatest(updated_at_unix_ms, lexarbor_now_unix_ms())
            WHERE target_id IN (SELECT id FROM enrichment_target WHERE word_id = OLD.id)
                AND state IN ('Pending', 'Running', 'Waiting');
            RETURN OLD;
        END $$;
        CREATE TRIGGER enrichment_word_deleted AFTER DELETE ON vocabulary
            FOR EACH ROW EXECUTE FUNCTION lexarbor_enrichment_word_deleted();

        CREATE FUNCTION lexarbor_enrichment_meaning_deleted() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            UPDATE enrichment_target SET deleted = true, epoch = epoch + 1
            WHERE meaning_id = OLD.id AND NOT deleted;
            UPDATE enrichment_round SET state = 'Cancelled', lease_owner = NULL,
                lease_expires_at_unix_ms = NULL, updated_at_unix_ms = greatest(updated_at_unix_ms, lexarbor_now_unix_ms())
            WHERE target_id IN (SELECT id FROM enrichment_target WHERE meaning_id = OLD.id)
                AND state IN ('Pending', 'Running', 'Waiting');
            RETURN OLD;
        END $$;
        CREATE TRIGGER enrichment_meaning_deleted AFTER DELETE ON vocabulary_meaning
            FOR EACH ROW EXECUTE FUNCTION lexarbor_enrichment_meaning_deleted();

        CREATE FUNCTION lexarbor_meaning_submission_ai_review_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'AI review conclusions are immutable.' USING ERRCODE = '23000';
        END $$;
        CREATE TRIGGER meaning_submission_ai_review_no_update BEFORE UPDATE ON meaning_submission_ai_review
            FOR EACH ROW EXECUTE FUNCTION lexarbor_meaning_submission_ai_review_immutable();
        CREATE TRIGGER meaning_submission_ai_review_no_delete BEFORE DELETE ON meaning_submission_ai_review
            FOR EACH ROW EXECUTE FUNCTION lexarbor_meaning_submission_ai_review_immutable();

        CREATE FUNCTION lexarbor_vocabulary_relation_meaning_invalidate() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            UPDATE vocabulary_relation
            SET version = version + 1,
                updated_at_unix_ms = greatest(updated_at_unix_ms, lexarbor_now_unix_ms())
            WHERE id IN (
                SELECT relation_id FROM vocabulary_relation_meaning
                WHERE source_meaning_id = OLD.id OR target_meaning_id = OLD.id);
            DELETE FROM vocabulary_relation_meaning
            WHERE source_meaning_id = OLD.id OR target_meaning_id = OLD.id;
            RETURN OLD;
        END $$;
        CREATE TRIGGER vocabulary_relation_meaning_deleted BEFORE DELETE ON vocabulary_meaning
            FOR EACH ROW EXECUTE FUNCTION lexarbor_vocabulary_relation_meaning_invalidate();
        CREATE TRIGGER vocabulary_relation_meaning_invalidated
            AFTER UPDATE OF meaning, part_of_speech, example, vocabulary_id, book_id ON vocabulary_meaning
            FOR EACH ROW
            WHEN (NEW.normalized_meaning IS DISTINCT FROM OLD.normalized_meaning
                OR NEW.normalized_part_of_speech IS DISTINCT FROM OLD.normalized_part_of_speech
                OR trim(NEW.example) IS DISTINCT FROM trim(OLD.example)
                OR NEW.vocabulary_id IS DISTINCT FROM OLD.vocabulary_id OR NEW.book_id IS DISTINCT FROM OLD.book_id)
            EXECUTE FUNCTION lexarbor_vocabulary_relation_meaning_invalidate();

        CREATE FUNCTION lexarbor_vocabulary_relation_meaning_scope_changed() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            DELETE FROM vocabulary_relation_meaning WHERE relation_id = OLD.id;
            RETURN NEW;
        END $$;
        CREATE TRIGGER vocabulary_relation_meaning_scope_changed
            AFTER UPDATE OF source_word_id, target_word_id, type, usage_note, status ON vocabulary_relation
            FOR EACH ROW
            WHEN (NEW.status <> 'confirmed' OR NEW.source_word_id IS DISTINCT FROM OLD.source_word_id
                OR NEW.target_word_id IS DISTINCT FROM OLD.target_word_id OR NEW.type IS DISTINCT FROM OLD.type
                OR NEW.usage_note IS DISTINCT FROM OLD.usage_note)
            EXECUTE FUNCTION lexarbor_vocabulary_relation_meaning_scope_changed();
        """;

    internal const string Down = """
        DROP TRIGGER IF EXISTS vocabulary_relation_meaning_scope_changed ON vocabulary_relation;
        DROP TRIGGER IF EXISTS vocabulary_relation_meaning_invalidated ON vocabulary_meaning;
        DROP TRIGGER IF EXISTS vocabulary_relation_meaning_deleted ON vocabulary_meaning;
        DROP TRIGGER IF EXISTS meaning_submission_ai_review_no_delete ON meaning_submission_ai_review;
        DROP TRIGGER IF EXISTS meaning_submission_ai_review_no_update ON meaning_submission_ai_review;
        DROP TRIGGER IF EXISTS enrichment_meaning_deleted ON vocabulary_meaning;
        DROP TRIGGER IF EXISTS enrichment_word_deleted ON vocabulary;
        DROP TRIGGER IF EXISTS audio_invalidation_retain ON audio_invalidation;
        DROP TRIGGER IF EXISTS audio_invalidation_immutable ON audio_invalidation;
        DROP TRIGGER IF EXISTS audio_invalidation_claim ON audio_invalidation;
        DROP TRIGGER IF EXISTS audio_invalidation_authorize ON audio_invalidation;
        DROP TRIGGER IF EXISTS audio_invalidation_asset_location ON audio_asset;
        DROP TRIGGER IF EXISTS audio_binding_invalidation_update ON vocabulary_audio_binding;
        DROP TRIGGER IF EXISTS audio_binding_invalidation_insert ON vocabulary_audio_binding;
        DROP FUNCTION IF EXISTS lexarbor_vocabulary_relation_meaning_scope_changed();
        DROP FUNCTION IF EXISTS lexarbor_vocabulary_relation_meaning_invalidate();
        DROP FUNCTION IF EXISTS lexarbor_meaning_submission_ai_review_immutable();
        DROP FUNCTION IF EXISTS lexarbor_enrichment_meaning_deleted();
        DROP FUNCTION IF EXISTS lexarbor_enrichment_word_deleted();
        DROP FUNCTION IF EXISTS lexarbor_audio_invalidation_retain();
        DROP FUNCTION IF EXISTS lexarbor_audio_invalidation_immutable();
        DROP FUNCTION IF EXISTS lexarbor_audio_invalidation_claim();
        DROP FUNCTION IF EXISTS lexarbor_audio_invalidation_authorize();
        DROP FUNCTION IF EXISTS lexarbor_audio_invalidation_asset_location();
        DROP FUNCTION IF EXISTS lexarbor_audio_binding_invalidation();
        DROP FUNCTION IF EXISTS lexarbor_now_unix_ms();
        """;
}
