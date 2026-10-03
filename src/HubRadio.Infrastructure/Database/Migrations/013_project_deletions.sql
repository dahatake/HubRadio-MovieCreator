CREATE TABLE project_creation_draft_deletions (
    draft_id TEXT NOT NULL PRIMARY KEY,
    deleted_at TEXT NOT NULL CHECK (
        length(deleted_at) > 0
        AND trim(deleted_at) = deleted_at
    ),
    FOREIGN KEY (draft_id) REFERENCES project_creation_drafts (id)
        ON UPDATE RESTRICT ON DELETE RESTRICT
) STRICT;

-- A deletion tombstone is permanent: it cannot be changed or removed, so a
-- deleted project is never restored by a later write.
CREATE TRIGGER trg_project_creation_draft_deletions_update
BEFORE UPDATE ON project_creation_draft_deletions
BEGIN
    SELECT RAISE(ABORT, 'project deletion records are immutable');
END;

CREATE TRIGGER trg_project_creation_draft_deletions_delete
BEFORE DELETE ON project_creation_draft_deletions
BEGIN
    SELECT RAISE(ABORT, 'project deletion records cannot be removed');
END;
