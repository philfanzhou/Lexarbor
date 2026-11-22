-- Version 5 keeps each fact in one place and indexes the issued-question lookups.
-- It runs once per learner schema inside the upgrade transaction. Plan and notebook
-- steps touch only legacy rows; dropping columns changes metadata only.

-- 1. Plans: `segments` is the only scope. A row written before multi-book scopes kept
-- its one book in `book_id`/`unit_ids` and an empty `segments`; readers fell back to it.
UPDATE vocab_plan
SET segments = jsonb_build_array(jsonb_build_object(
        'bookId', book_id,
        'unitIds', CASE
            WHEN unit_ids IS NULL OR unit_ids = '' THEN 'null'::jsonb
            WHEN jsonb_typeof(unit_ids::jsonb) <> 'array' THEN 'null'::jsonb
            WHEN jsonb_array_length(unit_ids::jsonb) = 0 THEN 'null'::jsonb
            ELSE unit_ids::jsonb
        END))::text
WHERE segments IN ('', '[]', 'null') AND book_id ~ '[^[:space:]]';
-- Without a book these rows were review-only scopes; readers now parse `segments` directly.
UPDATE vocab_plan SET segments = '[]' WHERE segments IN ('', 'null');

ALTER TABLE vocab_plan DROP COLUMN book_id, DROP COLUMN unit_ids;

-- 2. Notebook: family sources live only in vocab_notebook_family_source. An item written
-- before that table kept its one source in the item columns; readers used them only when
-- the item had no source rows. The old item check guarantees a ready item with a location.
INSERT INTO vocab_notebook_family_source (
    id, notebook_item_id, source_word_key, source_word, source_word_id, relation_type, direction,
    relation_id, source_meaning_id, source_book_id, target_word_id, target_meaning_id, target_book_id)
SELECT gen_random_uuid(), item.id, item.family_source_word_key, item.family_source_word,
    item.family_source_word_id, item.family_relation_type, item.family_direction, item.family_relation_id,
    coalesce(item.family_source_meaning_id, ''), coalesce(item.family_source_book_id, ''),
    item.word_id, item.meaning_id, item.book_id
FROM vocab_notebook_item item
WHERE item.family_source_word_key IS NOT NULL
    AND NOT EXISTS (SELECT 1 FROM vocab_notebook_family_source source WHERE source.notebook_item_id = item.id);

ALTER TABLE vocab_notebook_item
    DROP CONSTRAINT "CK_vocab_notebook_item_family",
    DROP CONSTRAINT "CK_vocab_notebook_item_family_meaning_pair",
    DROP COLUMN family_source_word_key,
    DROP COLUMN family_source_word,
    DROP COLUMN family_source_word_id,
    DROP COLUMN family_relation_type,
    DROP COLUMN family_direction,
    DROP COLUMN family_relation_id,
    DROP COLUMN family_source_meaning_id,
    DROP COLUMN family_source_book_id;

-- The dropped item check also required that a family-only item has a source, that only a
-- ready item has sources and that a source is another word. Both tables change in one
-- transaction, so the rule is checked per item at commit.
CREATE FUNCTION vocab_notebook_family_verify(item_schema text, item_id uuid) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE
    item_status smallint;
    item_family_only boolean;
    item_word_key text;
    source_count bigint;
    self_count bigint;
BEGIN
    EXECUTE format('SELECT status, family_only, word_key FROM %I.vocab_notebook_item WHERE id = $1', item_schema)
        INTO item_status, item_family_only, item_word_key USING item_id;
    -- EXECUTE does not set FOUND; status is never null on a stored item.
    IF item_status IS NULL THEN
        RETURN;
    END IF;
    EXECUTE format('SELECT count(*), count(*) FILTER (WHERE source_word_key = $2)
        FROM %I.vocab_notebook_family_source WHERE notebook_item_id = $1', item_schema)
        INTO source_count, self_count USING item_id, item_word_key;
    IF self_count > 0 OR (source_count > 0 AND item_status <> 1) OR (item_family_only AND source_count = 0) THEN
        RAISE EXCEPTION 'Notebook item % has an invalid family source.', item_id
            USING ERRCODE = 'check_violation', CONSTRAINT = 'CK_vocab_notebook_item_family';
    END IF;
END;
$$;

CREATE FUNCTION vocab_notebook_item_family_check() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    EXECUTE format('SELECT %I.vocab_notebook_family_verify($1, $2)', TG_TABLE_SCHEMA) USING TG_TABLE_SCHEMA, NEW.id;
    RETURN NULL;
END;
$$;

CREATE FUNCTION vocab_notebook_source_family_check() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'INSERT' THEN
        EXECUTE format('SELECT %I.vocab_notebook_family_verify($1, $2)', TG_TABLE_SCHEMA)
            USING TG_TABLE_SCHEMA, OLD.notebook_item_id;
    END IF;
    IF TG_OP <> 'DELETE' THEN
        EXECUTE format('SELECT %I.vocab_notebook_family_verify($1, $2)', TG_TABLE_SCHEMA)
            USING TG_TABLE_SCHEMA, NEW.notebook_item_id;
    END IF;
    RETURN NULL;
END;
$$;

CREATE CONSTRAINT TRIGGER "TR_vocab_notebook_item_family"
    AFTER INSERT OR UPDATE ON vocab_notebook_item
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION vocab_notebook_item_family_check();

CREATE CONSTRAINT TRIGGER "TR_vocab_notebook_family_source_item"
    AFTER INSERT OR UPDATE OR DELETE ON vocab_notebook_family_source
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION vocab_notebook_source_family_check();

-- 3. Issued questions: the open daily questions of a plan, the open questions of a learner
-- (free review) and the questions of one card.
-- An operator may build these beforehand with CREATE INDEX CONCURRENTLY under the same
-- names; a build that failed leaves an invalid index, which is rebuilt here.
DO $$
DECLARE
    index_name text;
BEGIN
    FOREACH index_name IN ARRAY ARRAY['ix_vocab_issued_question_plan_open', 'ix_vocab_issued_question_student_open',
            'ix_vocab_issued_question_student_card'] LOOP
        IF EXISTS (SELECT 1 FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
                WHERE c.relnamespace = current_schema()::regnamespace AND c.relname = index_name AND NOT i.indisvalid) THEN
            EXECUTE format('DROP INDEX %I', index_name);
        END IF;
    END LOOP;
END;
$$;

CREATE INDEX IF NOT EXISTS ix_vocab_issued_question_plan_open
    ON vocab_issued_question (plan_id, kind, issued_at, id) WHERE answered_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_vocab_issued_question_student_open
    ON vocab_issued_question (student_id, kind, issued_at, id) WHERE answered_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_vocab_issued_question_student_card
    ON vocab_issued_question (student_id, word_key, meaning_key, issued_at DESC, id DESC);

UPDATE learning_schema_version SET version = 5 WHERE version = 4;
