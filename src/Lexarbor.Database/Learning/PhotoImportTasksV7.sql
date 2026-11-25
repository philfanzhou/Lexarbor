CREATE TABLE vocab_photo_task (
    id uuid PRIMARY KEY,
    student_id uuid NOT NULL REFERENCES learner_identity(id) ON DELETE CASCADE,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    state text NOT NULL DEFAULT 'queued' CHECK (state IN ('queued','processing','failed','ready')),
    attempts integer NOT NULL DEFAULT 0,
    error_code text,
    photo bytea,
    entries jsonb NOT NULL DEFAULT '[]',
    added_count integer NOT NULL DEFAULT 0,
    CHECK ((state = 'ready') = (photo IS NULL)),
    CHECK ((state = 'failed') = (error_code IS NOT NULL)),
    CHECK (added_count BETWEEN 0 AND jsonb_array_length(entries))
);
CREATE INDEX vocab_photo_task_student ON vocab_photo_task(student_id, created_at DESC, id);
CREATE INDEX vocab_photo_task_queue ON vocab_photo_task(created_at, id) WHERE state IN ('queued','processing');
UPDATE learning_schema_version SET version = 7 WHERE version = 6;
