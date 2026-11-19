CREATE TABLE learner_archive_set (
    student_id uuid NOT NULL REFERENCES learner_identity(id) ON DELETE CASCADE,
    snapshot_id uuid NOT NULL,
    relation_name text NOT NULL,
    columns_json text NOT NULL,
    row_count bigint NOT NULL CHECK (row_count >= 0),
    sha256 text NOT NULL,
    PRIMARY KEY (student_id, snapshot_id, relation_name)
);
CREATE TABLE learner_archive_row (
    student_id uuid NOT NULL,
    snapshot_id uuid NOT NULL,
    relation_name text NOT NULL,
    ordinal bigint NOT NULL CHECK (ordinal > 0),
    payload text NOT NULL,
    PRIMARY KEY (student_id, snapshot_id, relation_name, ordinal),
    FOREIGN KEY (student_id, snapshot_id, relation_name)
        REFERENCES learner_archive_set ON DELETE CASCADE
);
UPDATE learning_schema_version SET version = 2 WHERE version = 1;
