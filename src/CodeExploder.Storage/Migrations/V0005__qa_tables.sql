-- M4 knowledge base + Q&A (docs/03, docs/06). 768-dim nomic-embed-text vectors with
-- HNSW cosine indexes; chat threads/messages with streaming-aware status.

create extension if not exists vector;

alter table chunks add column embedding vector(768);
create index chunks_embedding_idx on chunks using hnsw (embedding vector_cosine_ops);

alter table summaries add column embedding vector(768);
alter table sections add column embedding vector(768);

create table qa_threads (
    id         uuid primary key,
    session_id uuid not null references sessions(id) on delete cascade,
    user_id    uuid not null references users(id),
    title      text not null,
    created_at timestamptz not null default now()
);
create index qa_threads_session_idx on qa_threads (session_id, created_at desc);

create table qa_messages (
    id                uuid primary key,
    thread_id         uuid not null references qa_threads(id) on delete cascade,
    ord               int not null,
    role              text not null check (role in ('user','assistant')),
    content           text not null default '',
    citations         jsonb,
    status            text not null check (status in ('streaming','complete','error','cancelled')),
    section_context   uuid,
    prompt_tokens     int,
    completion_tokens int,
    created_at        timestamptz not null default now()
);
create index qa_messages_thread_idx on qa_messages (thread_id, ord);
