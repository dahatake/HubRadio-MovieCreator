CREATE TABLE project_creation_drafts (
    id TEXT NOT NULL PRIMARY KEY CHECK (
        length(id) = 36
        AND id = lower(id)
        AND substr(id, 9, 1) = '-'
        AND substr(id, 14, 1) = '-'
        AND substr(id, 19, 1) = '-'
        AND substr(id, 24, 1) = '-'
        AND length(replace(id, '-', '')) = 32
        AND replace(id, '-', '') NOT GLOB '*[^0-9a-f]*'
        AND id <> '00000000-0000-0000-0000-000000000000'
    ),
    revision INTEGER NOT NULL CHECK (revision >= 0),
    payload BLOB NOT NULL CHECK (length(payload) BETWEEN 1 AND 65536),
    updated_at TEXT NOT NULL CHECK (
        length(updated_at) > 0
        AND trim(updated_at) = updated_at
    ),
    finalized_project_id TEXT UNIQUE CHECK (
        finalized_project_id IS NULL
        OR (
            length(finalized_project_id) = 36
            AND finalized_project_id = lower(finalized_project_id)
            AND substr(finalized_project_id, 9, 1) = '-'
            AND substr(finalized_project_id, 14, 1) = '-'
            AND substr(finalized_project_id, 19, 1) = '-'
            AND substr(finalized_project_id, 24, 1) = '-'
            AND length(replace(finalized_project_id, '-', '')) = 32
            AND replace(finalized_project_id, '-', '') NOT GLOB '*[^0-9a-f]*'
            AND finalized_project_id <> '00000000-0000-0000-0000-000000000000'
        )
    ),
    finalized_at TEXT CHECK (
        finalized_at IS NULL
        OR (length(finalized_at) > 0 AND trim(finalized_at) = finalized_at)
    ),
    CHECK (
        (finalized_project_id IS NULL AND finalized_at IS NULL)
        OR (
            finalized_project_id IS NOT NULL
            AND finalized_at IS NOT NULL
            AND finalized_at = updated_at
        )
    ),
    FOREIGN KEY (finalized_project_id) REFERENCES projects (id)
        ON UPDATE RESTRICT ON DELETE RESTRICT
) STRICT;

CREATE INDEX idx_project_creation_drafts_updated_at
    ON project_creation_drafts (updated_at, id);