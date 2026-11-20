CREATE TABLE vocab_unit_practice (
    student_id uuid PRIMARY KEY REFERENCES learner_identity(id) ON DELETE CASCADE,
    session_id uuid UNIQUE NOT NULL,
    book_id text NOT NULL,
    unit_id text NOT NULL,
    round integer NOT NULL DEFAULT 1
);
CREATE TABLE vocab_unit_practice_item (
    session_id uuid NOT NULL REFERENCES vocab_unit_practice(session_id) ON DELETE CASCADE,
    position integer NOT NULL,
    word_id text NOT NULL,
    meaning_id text NOT NULL,
    word text NOT NULL,
    meaning text NOT NULL,
    part_of_speech text,
    spelling_hint text,
    first_question_id uuid NOT NULL,
    first_correct boolean,
    retry_question_id uuid,
    retry_round integer NOT NULL DEFAULT 0,
    retry_correct boolean,
    PRIMARY KEY (session_id, position)
);
UPDATE learning_schema_version SET version = 3 WHERE version = 2;
