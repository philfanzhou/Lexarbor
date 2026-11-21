ALTER TABLE vocab_unit_practice ADD COLUMN mode text NOT NULL DEFAULT 'spelling'
    CHECK (mode IN ('spelling', 'choice', 'mixed'));
ALTER TABLE vocab_unit_practice_item ADD COLUMN first_question jsonb, ADD COLUMN retry_question jsonb;
UPDATE learning_schema_version SET version = 4 WHERE version = 3;
