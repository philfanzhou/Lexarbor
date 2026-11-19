CREATE TABLE learner_identity (
    id uuid NOT NULL
);

CREATE TABLE vocab_attempt (
    issued_question_id uuid NOT NULL,
    result smallint NOT NULL,
    selected_option_id uuid,
    elapsed_ms integer NOT NULL,
    rating smallint NOT NULL,
    answered_at timestamp with time zone NOT NULL,
    local_date date NOT NULL,
    filled_letters text
);

CREATE TABLE vocab_issued_question (
    id uuid NOT NULL,
    student_id uuid NOT NULL,
    plan_id uuid,
    kind smallint NOT NULL,
    word_key text NOT NULL,
    meaning_key text NOT NULL,
    word_id text NOT NULL,
    meaning_id text NOT NULL,
    book_id text NOT NULL,
    unit_id text,
    direction smallint NOT NULL,
    prompt text NOT NULL,
    options_snapshot text,
    correct_option_id uuid,
    issued_at timestamp with time zone NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    answered_at timestamp with time zone,
    letter_pattern text,
    answer_word text,
    anchor_word text,
    part_of_speech text,
    relation_type text,
    family_source_word text,
    family_source_relation_type text,
    family_source_direction text,
    CONSTRAINT "CK_vocab_issued_question_answer_shape" CHECK ((((((((direction = ANY (ARRAY[1, 2])) AND (options_snapshot IS NOT NULL) AND (correct_option_id IS NOT NULL) AND (letter_pattern IS NULL) AND (answer_word IS NULL)) OR ((direction = 3) AND (options_snapshot IS NULL) AND (correct_option_id IS NULL) AND (letter_pattern IS NOT NULL) AND (answer_word IS NOT NULL))) AND (anchor_word IS NULL) AND (part_of_speech IS NULL)) OR ((direction = 7) AND (kind = 2) AND (options_snapshot IS NOT NULL) AND (correct_option_id IS NOT NULL) AND (letter_pattern IS NULL) AND (answer_word IS NULL) AND (anchor_word IS NOT NULL) AND (btrim(anchor_word) <> ''::text) AND (part_of_speech IS NOT NULL) AND (part_of_speech = ANY (ARRAY['noun'::text, 'verb'::text, 'adjective'::text, 'adverb'::text, 'pronoun'::text, 'preposition'::text, 'conjunction'::text, 'determiner'::text, 'article'::text, 'numeral'::text, 'interjection'::text])))) AND (relation_type IS NULL)) OR ((kind = 2) AND (part_of_speech IS NULL) AND (((direction = 4) AND (options_snapshot IS NOT NULL) AND (correct_option_id IS NOT NULL) AND (letter_pattern IS NULL) AND (answer_word IS NULL) AND (anchor_word IS NULL) AND (relation_type IS NOT NULL) AND (relation_type = ANY (ARRAY['derived_noun'::text, 'derived_verb'::text, 'derived_adjective'::text, 'derived_adverb'::text, 'past_tense'::text, 'past_participle'::text, 'ing_form'::text, 'third_person_singular'::text, 'plural'::text, 'comparative'::text, 'superlative'::text]))) OR ((direction = 5) AND (options_snapshot IS NOT NULL) AND (correct_option_id IS NOT NULL) AND (letter_pattern IS NULL) AND (answer_word IS NULL) AND (anchor_word IS NOT NULL) AND (btrim(anchor_word) <> ''::text) AND (relation_type IS NULL)) OR ((direction = 6) AND (options_snapshot IS NULL) AND (correct_option_id IS NULL) AND (letter_pattern IS NOT NULL) AND (answer_word IS NOT NULL) AND (anchor_word IS NOT NULL) AND (btrim(anchor_word) <> ''::text) AND (relation_type IS NOT NULL) AND (relation_type = ANY (ARRAY['derived_noun'::text, 'derived_verb'::text, 'derived_adjective'::text, 'derived_adverb'::text, 'past_tense'::text, 'past_participle'::text, 'ing_form'::text, 'third_person_singular'::text, 'plural'::text, 'comparative'::text, 'superlative'::text]))))))),
    CONSTRAINT "CK_vocab_issued_question_family_hint" CHECK ((((family_source_word IS NULL) AND (family_source_relation_type IS NULL) AND (family_source_direction IS NULL)) OR ((direction = 1) AND (kind = ANY (ARRAY[1, 3])) AND (family_source_word IS NOT NULL) AND (btrim(family_source_word) <> ''::text) AND (family_source_relation_type IS NOT NULL) AND (family_source_relation_type = ANY (ARRAY['derived_noun'::text, 'derived_verb'::text, 'derived_adjective'::text, 'derived_adverb'::text, 'past_tense'::text, 'past_participle'::text, 'ing_form'::text, 'third_person_singular'::text, 'plural'::text, 'comparative'::text, 'superlative'::text])) AND (family_source_direction IS NOT NULL) AND (family_source_direction = ANY (ARRAY['incoming'::text, 'outgoing'::text]))))),
    CONSTRAINT "CK_vocab_issued_question_free_review" CHECK (((plan_id IS NOT NULL) OR (kind = 2)))
);

CREATE TABLE vocab_memory_card (
    student_id uuid NOT NULL,
    word_key text NOT NULL,
    meaning_key text NOT NULL,
    state smallint NOT NULL,
    stability double precision,
    difficulty double precision,
    due_at timestamp with time zone NOT NULL,
    reps integer NOT NULL,
    lapses integer NOT NULL,
    last_reviewed_at timestamp with time zone NOT NULL,
    first_learned_at timestamp with time zone NOT NULL,
    first_learned_plan_id uuid,
    learning_step integer,
    version integer NOT NULL
);

CREATE TABLE vocab_notebook_family_source (
    id uuid NOT NULL,
    notebook_item_id uuid NOT NULL,
    source_word_key text NOT NULL,
    source_word text NOT NULL,
    source_word_id text NOT NULL,
    relation_type text NOT NULL,
    direction text NOT NULL,
    relation_id text,
    source_meaning_id text NOT NULL,
    source_book_id text NOT NULL,
    target_word_id text NOT NULL,
    target_meaning_id text NOT NULL,
    target_book_id text NOT NULL,
    CONSTRAINT "CK_vocab_notebook_family_source_shape" CHECK (((btrim(source_word_key) <> ''::text) AND (btrim(source_word) <> ''::text) AND (btrim(source_word_id) <> ''::text) AND (relation_type = ANY (ARRAY['derived_noun'::text, 'derived_verb'::text, 'derived_adjective'::text, 'derived_adverb'::text, 'past_tense'::text, 'past_participle'::text, 'ing_form'::text, 'third_person_singular'::text, 'plural'::text, 'comparative'::text, 'superlative'::text])) AND (direction = ANY (ARRAY['incoming'::text, 'outgoing'::text])) AND (btrim(target_word_id) <> ''::text) AND (btrim(target_meaning_id) <> ''::text) AND (btrim(target_book_id) <> ''::text) AND (((source_book_id = ''::text) AND (source_meaning_id = ''::text)) OR ((btrim(source_book_id) <> ''::text) AND (btrim(source_meaning_id) <> ''::text) AND (relation_id IS NOT NULL) AND (btrim(relation_id) <> ''::text)))))
);

CREATE TABLE vocab_notebook_item (
    id uuid NOT NULL,
    student_id uuid NOT NULL,
    word_key text NOT NULL,
    meaning_key text NOT NULL,
    word text NOT NULL,
    part_of_speech text,
    meaning text NOT NULL,
    status smallint NOT NULL,
    book_id text,
    word_id text,
    meaning_id text,
    submission_id text,
    added_by smallint NOT NULL,
    added_by_teacher_id uuid,
    added_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    review_comment text,
    original_word text,
    original_part_of_speech text,
    original_meaning text,
    family_source_word_key text,
    family_source_word text,
    family_source_word_id text,
    family_relation_type text,
    family_direction text,
    family_only boolean DEFAULT false NOT NULL,
    family_relation_id text,
    family_source_meaning_id text,
    family_source_book_id text,
    CONSTRAINT "CK_vocab_notebook_item_added_by" CHECK ((((added_by = 1) AND (added_by_teacher_id IS NULL)) OR ((added_by = 2) AND (added_by_teacher_id IS NOT NULL)))),
    CONSTRAINT "CK_vocab_notebook_item_family" CHECK ((((family_source_word_key IS NULL) AND (family_source_word IS NULL) AND (family_source_word_id IS NULL) AND (family_relation_type IS NULL) AND (family_direction IS NULL) AND (NOT family_only)) OR ((status = 1) AND (family_source_word_key IS NOT NULL) AND (btrim(family_source_word_key) <> ''::text) AND (family_source_word_key <> word_key) AND (family_source_word IS NOT NULL) AND (btrim(family_source_word) <> ''::text) AND (family_source_word_id IS NOT NULL) AND (btrim(family_source_word_id) <> ''::text) AND (family_relation_type IS NOT NULL) AND (family_relation_type = ANY (ARRAY['derived_noun'::text, 'derived_verb'::text, 'derived_adjective'::text, 'derived_adverb'::text, 'past_tense'::text, 'past_participle'::text, 'ing_form'::text, 'third_person_singular'::text, 'plural'::text, 'comparative'::text, 'superlative'::text])) AND (family_direction IS NOT NULL) AND (family_direction = ANY (ARRAY['incoming'::text, 'outgoing'::text]))))),
    CONSTRAINT "CK_vocab_notebook_item_family_meaning_pair" CHECK ((((family_relation_id IS NULL) AND (family_source_meaning_id IS NULL) AND (family_source_book_id IS NULL)) OR ((family_source_word_id IS NOT NULL) AND (family_relation_id IS NOT NULL) AND (btrim(family_relation_id) <> ''::text) AND (family_source_meaning_id IS NOT NULL) AND (btrim(family_source_meaning_id) <> ''::text) AND (family_source_book_id IS NOT NULL) AND (btrim(family_source_book_id) <> ''::text)))),
    CONSTRAINT "CK_vocab_notebook_item_location" CHECK ((((status = 1) AND (book_id IS NOT NULL) AND (word_id IS NOT NULL) AND (meaning_id IS NOT NULL) AND (submission_id IS NULL)) OR ((status = ANY (ARRAY[2, 3])) AND (submission_id IS NOT NULL) AND (book_id IS NULL) AND (word_id IS NULL) AND (meaning_id IS NULL)))),
    CONSTRAINT "CK_vocab_notebook_item_original" CHECK ((((original_word IS NULL) AND (original_part_of_speech IS NULL) AND (original_meaning IS NULL)) OR ((status = 1) AND (original_word IS NOT NULL) AND (original_meaning IS NOT NULL)))),
    CONSTRAINT "CK_vocab_notebook_item_review_comment" CHECK (((review_comment IS NULL) OR (status = 3) OR ((status = 1) AND (original_word IS NOT NULL))))
);

CREATE TABLE vocab_plan (
    id uuid NOT NULL,
    student_id uuid NOT NULL,
    book_id text NOT NULL,
    unit_ids text,
    daily_new_words integer NOT NULL,
    controller smallint NOT NULL,
    controller_teacher_id uuid,
    status smallint NOT NULL,
    start_date date NOT NULL,
    total_words integer NOT NULL,
    version integer NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    segments text NOT NULL
);

CREATE TABLE vocab_plan_change (
    id uuid NOT NULL,
    plan_id uuid NOT NULL,
    actor_type smallint NOT NULL,
    actor_teacher_id uuid,
    action text NOT NULL,
    before_values text,
    after_values text,
    created_at timestamp with time zone NOT NULL
);

CREATE TABLE vocab_plan_day (
    plan_id uuid NOT NULL,
    local_date date NOT NULL,
    new_words_target integer NOT NULL,
    new_words_learned integer NOT NULL,
    due_in_scope text NOT NULL,
    reviewed_in_scope text NOT NULL,
    completed_at timestamp with time zone,
    version integer NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    new_words_exhausted boolean DEFAULT false NOT NULL,
    daily_tasks text
);

CREATE TABLE vocab_plan_legacy_history (
    source_plan_id uuid NOT NULL,
    student_id uuid NOT NULL,
    target_plan_id uuid NOT NULL,
    status smallint NOT NULL,
    started_at timestamp with time zone NOT NULL,
    ended_at timestamp with time zone NOT NULL,
    completed_at timestamp with time zone,
    reactivated_at timestamp with time zone,
    completed_days jsonb NOT NULL,
    migrated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT vocab_plan_legacy_history_check CHECK (((completed_at IS NULL) = (reactivated_at IS NULL))),
    CONSTRAINT vocab_plan_legacy_history_completed_days_check CHECK ((jsonb_typeof(completed_days) = 'array'::text)),
    CONSTRAINT vocab_plan_legacy_history_status_check CHECK ((status = ANY (ARRAY[2, 3])))
);

CREATE TABLE vocab_review_log (
    id bigint NOT NULL,
    student_id uuid NOT NULL,
    word_key text NOT NULL,
    meaning_key text NOT NULL,
    rating smallint NOT NULL,
    reviewed_at timestamp with time zone NOT NULL,
    stability_before double precision,
    difficulty_before double precision,
    due_before timestamp with time zone,
    stability_after double precision NOT NULL,
    difficulty_after double precision NOT NULL,
    due_after timestamp with time zone NOT NULL,
    elapsed_days integer NOT NULL,
    state_before smallint,
    state_after smallint
);

ALTER TABLE vocab_review_log ALTER COLUMN id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME vocab_review_log_id_seq
    START WITH 4611686018427387904
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);

ALTER TABLE ONLY vocab_notebook_family_source
    ADD CONSTRAINT "PK_vocab_notebook_family_source" PRIMARY KEY (id);

ALTER TABLE ONLY vocab_notebook_item
    ADD CONSTRAINT "PK_vocab_notebook_item" PRIMARY KEY (id);

ALTER TABLE ONLY learner_identity
    ADD CONSTRAINT learner_identity_pkey PRIMARY KEY (id);

ALTER TABLE ONLY vocab_attempt
    ADD CONSTRAINT pk_vocab_attempt PRIMARY KEY (issued_question_id);

ALTER TABLE ONLY vocab_issued_question
    ADD CONSTRAINT pk_vocab_issued_question PRIMARY KEY (id);

ALTER TABLE ONLY vocab_memory_card
    ADD CONSTRAINT pk_vocab_memory_card PRIMARY KEY (student_id, word_key, meaning_key);

ALTER TABLE ONLY vocab_plan
    ADD CONSTRAINT pk_vocab_plan PRIMARY KEY (id);

ALTER TABLE ONLY vocab_plan_change
    ADD CONSTRAINT pk_vocab_plan_change PRIMARY KEY (id);

ALTER TABLE ONLY vocab_plan_day
    ADD CONSTRAINT pk_vocab_plan_day PRIMARY KEY (plan_id, local_date);

ALTER TABLE ONLY vocab_review_log
    ADD CONSTRAINT pk_vocab_review_log PRIMARY KEY (id);

ALTER TABLE ONLY vocab_plan_legacy_history
    ADD CONSTRAINT vocab_plan_legacy_history_pkey PRIMARY KEY (source_plan_id);

CREATE UNIQUE INDEX "IX_vocab_notebook_family_source_identity" ON vocab_notebook_family_source USING btree (notebook_item_id, source_word_id, relation_type, direction, target_book_id, target_word_id, target_meaning_id, source_book_id, source_meaning_id);

CREATE INDEX "IX_vocab_notebook_item_student_id_added_at" ON vocab_notebook_item USING btree (student_id, added_at);

CREATE UNIQUE INDEX "IX_vocab_notebook_item_student_id_word_key_meaning_key" ON vocab_notebook_item USING btree (student_id, word_key, meaning_key);

CREATE INDEX ix_vocab_issued_question_student_kind_issued_at ON vocab_issued_question USING btree (student_id, kind, issued_at);

CREATE INDEX ix_vocab_issued_question_student_word_kind ON vocab_issued_question USING btree (student_id, word_key, kind);

CREATE INDEX ix_vocab_legacy_history_student ON vocab_plan_legacy_history USING btree (student_id);

CREATE INDEX ix_vocab_memory_card_student_id_due_at ON vocab_memory_card USING btree (student_id, due_at);

CREATE INDEX ix_vocab_plan_change_plan_id_created_at ON vocab_plan_change USING btree (plan_id, created_at);

CREATE UNIQUE INDEX ix_vocab_plan_student_id_active ON vocab_plan USING btree (student_id) WHERE (status = 1);

CREATE INDEX ix_vocab_plan_student_id_status_updated_at ON vocab_plan USING btree (student_id, status, updated_at);

CREATE INDEX ix_vocab_review_log_student_id_word_key_meaning_key ON vocab_review_log USING btree (student_id, word_key, meaning_key);

ALTER TABLE ONLY vocab_notebook_family_source
    ADD CONSTRAINT "FK_vocab_notebook_family_source_item" FOREIGN KEY (notebook_item_id) REFERENCES vocab_notebook_item(id) ON DELETE CASCADE;

ALTER TABLE ONLY vocab_notebook_item
    ADD CONSTRAINT "FK_vocab_notebook_item_learner_identity_student_id" FOREIGN KEY (student_id) REFERENCES learner_identity(id) ON DELETE CASCADE;

ALTER TABLE ONLY vocab_attempt
    ADD CONSTRAINT fk_vocab_attempt_vocab_issued_question_issued_question_id FOREIGN KEY (issued_question_id) REFERENCES vocab_issued_question(id) ON DELETE CASCADE;

ALTER TABLE ONLY vocab_issued_question
    ADD CONSTRAINT fk_vocab_issued_question_learner_identity_student_id FOREIGN KEY (student_id) REFERENCES learner_identity(id) ON DELETE CASCADE;

ALTER TABLE ONLY vocab_issued_question
    ADD CONSTRAINT fk_vocab_issued_question_vocab_plan_plan_id FOREIGN KEY (plan_id) REFERENCES vocab_plan(id) ON DELETE CASCADE;

ALTER TABLE ONLY vocab_memory_card
    ADD CONSTRAINT fk_vocab_memory_card_learner_identity_student_id FOREIGN KEY (student_id) REFERENCES learner_identity(id) ON DELETE CASCADE;

ALTER TABLE ONLY vocab_plan_change
    ADD CONSTRAINT fk_vocab_plan_change_vocab_plan_plan_id FOREIGN KEY (plan_id) REFERENCES vocab_plan(id) ON DELETE CASCADE;

ALTER TABLE ONLY vocab_plan_day
    ADD CONSTRAINT fk_vocab_plan_day_vocab_plan_plan_id FOREIGN KEY (plan_id) REFERENCES vocab_plan(id) ON DELETE CASCADE;

ALTER TABLE ONLY vocab_plan
    ADD CONSTRAINT fk_vocab_plan_learner_identity_student_id FOREIGN KEY (student_id) REFERENCES learner_identity(id) ON DELETE CASCADE;

ALTER TABLE ONLY vocab_review_log
    ADD CONSTRAINT fk_vocab_review_log_vocab_memory_card_student_id_word_key_meani FOREIGN KEY (student_id, word_key, meaning_key) REFERENCES vocab_memory_card(student_id, word_key, meaning_key) ON DELETE CASCADE;

ALTER TABLE ONLY vocab_plan_legacy_history
    ADD CONSTRAINT vocab_plan_legacy_history_student_id_fkey FOREIGN KEY (student_id) REFERENCES learner_identity(id) ON DELETE CASCADE;
