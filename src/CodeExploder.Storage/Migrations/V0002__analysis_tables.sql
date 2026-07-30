-- M1 deterministic-analysis tables (docs/03-data-model.md, analysis side).
-- pg_trgm backs identifier-shaped lexical search for Q&A retrieval (M4); creating it
-- here keeps chunk indexing one-shot. The embedding column arrives with M4.

create extension if not exists pg_trgm;

alter table analyses
    add column commit_sha text,
    add column plan jsonb,
    add column meta jsonb;

create table files (
    id             uuid primary key,
    analysis_id    uuid not null references analyses(id) on delete cascade,
    path           text not null,
    language       text not null,
    size_bytes     bigint not null,
    excluded       boolean not null default false,
    exclude_reason text,
    role           int not null default 0,
    churn          int not null default 0,
    rank           int not null default 0
);
create unique index files_analysis_path_idx on files (analysis_id, path);

create table chunks (
    id          uuid primary key,
    analysis_id uuid not null references analyses(id) on delete cascade,
    file_id     uuid not null references files(id) on delete cascade,
    start_line  int not null,
    end_line    int not null,
    content     text not null,
    token_count int not null,
    tsv         tsvector generated always as (to_tsvector('simple', content)) stored
);
create index chunks_analysis_idx on chunks (analysis_id);
create index chunks_tsv_idx on chunks using gin (tsv);
create index chunks_trgm_idx on chunks using gin (content gin_trgm_ops);

create table components (
    id          uuid primary key,
    analysis_id uuid not null references analyses(id) on delete cascade,
    name        text not null,
    root_paths  text[] not null,
    file_count  int not null,
    plan_rank   int not null default 0
);
create index components_analysis_idx on components (analysis_id);
