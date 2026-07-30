-- M0 core schema (docs/03-data-model.md, walking-skeleton subset).
-- sections_total/sections_ready on analyses are M0 placeholders for the progress
-- counts; the full experience-side tables (experiences/sections/blocks) arrive in M2.

create table users (
    id           uuid primary key,
    subject      text not null unique,
    display_name text not null,
    created_at   timestamptz not null default now()
);

create table repos (
    id         uuid primary key,
    owner      text not null,
    name       text not null,
    url        text not null,
    created_at timestamptz not null default now(),
    unique (owner, name)
);

create table analyses (
    id               uuid primary key,
    repo_id          uuid not null references repos(id),
    kind             text not null check (kind in ('repo','pr')),
    pr_number        int,
    status           text not null default 'planning'
                     check (status in ('planning','running','ready','ready_with_warnings','failed','cancelled')),
    cancel_requested boolean not null default false,
    sections_total   int not null default 0,
    sections_ready   int not null default 0,
    error            text,
    created_at       timestamptz not null default now(),
    finished_at      timestamptz
);

create table sessions (
    id             uuid primary key,
    user_id        uuid not null references users(id),
    analysis_id    uuid not null references analyses(id) on delete cascade,
    kind           text not null check (kind in ('repo','pr')),
    title          text not null,
    status         text not null default 'queued'
                   check (status in ('queued','analyzing','ready','partial','failed')),
    failure_reason text,
    created_at     timestamptz not null default now(),
    last_opened_at timestamptz not null default now()
);
create index sessions_user_idx on sessions (user_id, created_at desc);

-- The job queue (docs/02-queue-and-events.md): the reference queue schema plus the
-- counting-join extension (analysis_id, unblocks_job_id, blocked_count and the
-- 'blocked'/'cancelled' statuses).
create table jobs (
    id              uuid primary key,
    job_type        text not null,
    payload         jsonb not null,
    priority        int not null default 0,
    available_at    timestamptz,
    status          text not null default 'queued'
                    check (status in ('queued','blocked','running','succeeded','failed','cancelled')),
    attempts        int not null default 0,
    max_attempts    int not null default 3,
    analysis_id     uuid,
    unblocks_job_id uuid,
    blocked_count   int not null default 0,
    locked_by       text,
    locked_at       timestamptz,
    last_error      text,
    created_at      timestamptz not null default now(),
    updated_at      timestamptz not null default now()
);
create index jobs_dequeue_idx on jobs (job_type, priority desc, created_at) where status = 'queued';
create index jobs_analysis_idx on jobs (analysis_id) where analysis_id is not null;

-- Durable event log for SignalR reconnect catch-up (docs/04-api.md). Rows carry the
-- full envelope so GetEventsSince can replay them verbatim.
create table pipeline_events (
    id         bigserial primary key,
    session_id uuid not null,
    kind       text not null,
    payload    jsonb not null,
    at         timestamptz not null default now()
);
create index pipeline_events_session_idx on pipeline_events (session_id, id);
