-- M9: the origin-story section kind (docs/08 §M9).

alter table sections drop constraint sections_kind_check;
alter table sections add constraint sections_kind_check
    check (kind in ('intro','architecture','scenario','build',
                    'pr-overview','pr-walkthrough','pr-risk','story'));
