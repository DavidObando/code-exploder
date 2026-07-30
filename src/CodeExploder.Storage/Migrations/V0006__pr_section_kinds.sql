-- M5: the PR-diff explainer's section kinds (docs/01 §PR-diff mode).

alter table sections drop constraint sections_kind_check;
alter table sections add constraint sections_kind_check
    check (kind in ('intro','architecture','scenario','build',
                    'pr-overview','pr-walkthrough','pr-risk'));
