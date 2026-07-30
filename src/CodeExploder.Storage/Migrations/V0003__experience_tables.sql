-- M2 experience-side tables (docs/03-data-model.md): LLM summaries, the versioned
-- generated tutorial (experiences → sections → blocks), and per-user section
-- progress. Quizzes arrive in M3; embeddings in M4.

create table summaries (
    id             uuid primary key,
    analysis_id    uuid not null references analyses(id) on delete cascade,
    scope          text not null check (scope in ('component','repo')),
    component_id   uuid references components(id) on delete cascade,
    prose_md       text not null default '',
    structured     jsonb,
    model          text not null,
    prompt_version text not null,
    created_at     timestamptz not null default now()
);
create index summaries_analysis_idx on summaries (analysis_id, scope);

create table experiences (
    id           uuid primary key,
    session_id   uuid not null references sessions(id) on delete cascade,
    version      int not null default 1,
    commit_sha   text not null,
    model        text not null,
    generated_at timestamptz not null default now(),
    unique (session_id, version)
);

create table sections (
    id                uuid primary key,
    experience_id     uuid not null references experiences(id) on delete cascade,
    parent_section_id uuid references sections(id) on delete cascade,
    ord               int not null,
    depth             int not null default 0,
    slug              text not null,
    kind              text not null check (kind in ('intro','architecture','scenario','build')),
    title             text not null,
    summary           text not null default '',
    estimated_minutes int not null default 0,
    status            text not null default 'pending'
                      check (status in ('pending','generating','ready','failed')),
    unique (experience_id, slug)
);
create index sections_experience_idx on sections (experience_id, ord);

create table blocks (
    id         uuid primary key,
    section_id uuid not null references sections(id) on delete cascade,
    ord        int not null,
    type       text not null check (type in ('markdown','diagram','code','callout')),
    data       jsonb not null
);
create index blocks_section_idx on blocks (section_id, ord);

create table section_progress (
    user_id    uuid not null references users(id),
    section_id uuid not null references sections(id) on delete cascade,
    state      text not null check (state in ('unread','read','skipped','completed')),
    updated_at timestamptz not null default now(),
    primary key (user_id, section_id)
);
