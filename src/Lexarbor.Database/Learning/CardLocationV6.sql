-- Version 6 records where each card is reviewed and the source it was learned from (#443).
-- Before it, reviews took the location of the card's latest question, which moved with every
-- question. Adding a nullable column changes metadata only.

-- 1 plan, 2 notebook, 3 notebook item added only through a word family; null = not recorded.
ALTER TABLE vocab_issued_question ADD COLUMN source smallint
    CONSTRAINT "CK_vocab_issued_question_source" CHECK (source IS NULL OR source IN (1, 2, 3));

CREATE TABLE vocab_card_location (
    student_id uuid NOT NULL,
    word_key text NOT NULL,
    meaning_key text NOT NULL,
    book_id text NOT NULL CHECK (btrim(book_id) <> ''),
    word_id text NOT NULL CHECK (btrim(word_id) <> ''),
    meaning_id text NOT NULL CHECK (btrim(meaning_id) <> ''),
    source smallint CONSTRAINT "CK_vocab_card_location_source" CHECK (source IS NULL OR source IN (1, 2, 3)),
    -- The question the location was taken from; no foreign key, so it survives question cleanup.
    source_question_id uuid,
    recorded_at timestamp with time zone NOT NULL,
    CONSTRAINT pk_vocab_card_location PRIMARY KEY (student_id, word_key, meaning_key),
    CONSTRAINT fk_vocab_card_location_vocab_memory_card FOREIGN KEY (student_id, word_key, meaning_key)
        REFERENCES vocab_memory_card (student_id, word_key, meaning_key) ON DELETE CASCADE
);

-- Each existing card takes the location of the question that created it: its earliest answered new or
-- extra question. A card without one takes its latest answered question. A card with no answered
-- question gets no row and keeps the read-time fallback. History did not record sources, so they stay null.
INSERT INTO vocab_card_location (student_id, word_key, meaning_key, book_id, word_id, meaning_id, source,
    source_question_id, recorded_at)
SELECT card.student_id, card.word_key, card.meaning_key, chosen.book_id, chosen.word_id, chosen.meaning_id, NULL,
    chosen.id, chosen.answered_at
FROM vocab_memory_card card
CROSS JOIN LATERAL (
    SELECT question.id, question.book_id, question.word_id, question.meaning_id, question.answered_at
    FROM vocab_issued_question question
    WHERE question.student_id = card.student_id
        AND question.word_key = card.word_key
        AND question.meaning_key = card.meaning_key
        AND question.answered_at IS NOT NULL
        AND btrim(question.book_id) <> '' AND btrim(question.word_id) <> '' AND btrim(question.meaning_id) <> ''
    ORDER BY question.kind IN (1, 3) DESC,
        CASE WHEN question.kind IN (1, 3) THEN question.issued_at END ASC,
        CASE WHEN question.kind NOT IN (1, 3) THEN question.issued_at END DESC,
        question.id
    LIMIT 1
) chosen;

UPDATE learning_schema_version SET version = 6 WHERE version = 5;
