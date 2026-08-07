-- M10: recursive scope explosion (deep dives, docs/08 §M10).

alter table components
    add column parent_component_id uuid references components(id) on delete cascade,
    add column depth int not null default 0;
create index components_parent_idx on components (parent_component_id)
    where parent_component_id is not null;

alter table sections
    add column component_id uuid references components(id) on delete set null;

alter table sections drop constraint sections_kind_check;
alter table sections add constraint sections_kind_check
    check (kind in ('intro','architecture','scenario','build',
                    'pr-overview','pr-walkthrough','pr-risk','story',
                    'deep-dive','deep-dive-tour','deep-dive-flow','deep-dive-interfaces'));

alter table summaries drop constraint summaries_scope_check;
alter table summaries add constraint summaries_scope_check
    check (scope in ('component','repo','scope'));

-- One explosion per (experience, component): the dedup key, the status machine,
-- and the subtree progress counters. Everything cascades away with the analysis,
-- so session retry/delete needs no extra cleanup.
create table explosions (
    id                  uuid primary key,
    analysis_id         uuid not null references analyses(id) on delete cascade,
    experience_id       uuid not null references experiences(id) on delete cascade,
    component_id        uuid not null references components(id) on delete cascade,
    section_id          uuid references sections(id) on delete cascade,
    parent_explosion_id uuid references explosions(id) on delete cascade,
    depth               int not null default 1 check (depth between 1 and 3),
    trigger             text not null check (trigger in ('eager','on_demand')),
    status              text not null default 'queued'
                        check (status in ('queued','running','ready','partial','failed')),
    sections_total      int not null default 0,
    sections_ready      int not null default 0,
    queue_job_id        uuid,
    error               text,
    created_at          timestamptz not null default now(),
    finished_at         timestamptz,
    unique (experience_id, component_id)
);
create index explosions_analysis_idx on explosions (analysis_id);
