-- M3 quiz tables (docs/03-data-model.md). Answer keys and rubrics live in
-- quiz_questions.data and are stripped before anything reaches the client.

create table quizzes (
    id         uuid primary key,
    section_id uuid not null unique references sections(id) on delete cascade,
    title      text not null,
    created_at timestamptz not null default now()
);

create table quiz_questions (
    id          uuid primary key,
    quiz_id     uuid not null references quizzes(id) on delete cascade,
    ord         int not null,
    type        text not null check (type in ('single','multi','boolean','short')),
    prompt      text not null,
    data        jsonb not null,
    section_ref uuid,
    block_ref   uuid
);
create index quiz_questions_quiz_idx on quiz_questions (quiz_id, ord);

create table quiz_attempts (
    id           uuid primary key,
    quiz_id      uuid not null references quizzes(id) on delete cascade,
    user_id      uuid not null references users(id),
    answers      jsonb not null,
    submitted_at timestamptz not null default now(),
    status       text not null check (status in ('grading','graded')),
    score_pct    int,
    feedback     jsonb
);
create index quiz_attempts_quiz_user_idx on quiz_attempts (quiz_id, user_id, submitted_at desc);

alter table section_progress add column quiz_best_pct int;
