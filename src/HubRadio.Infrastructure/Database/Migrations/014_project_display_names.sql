-- A display name is a presentation-only label for a project. The stored draft,
-- its source-only digests and the finalized project snapshot are never changed,
-- so renaming cannot invalidate an execution that is bound to the original name.
CREATE TABLE project_display_names (
    draft_id TEXT NOT NULL PRIMARY KEY,
    display_name TEXT NOT NULL CHECK (
        length(display_name) BETWEEN 1 AND 100
        AND trim(display_name) = display_name
    ),
    updated_at TEXT NOT NULL CHECK (
        length(updated_at) > 0
        AND trim(updated_at) = updated_at
    ),
    FOREIGN KEY (draft_id) REFERENCES project_creation_drafts (id)
        ON UPDATE RESTRICT ON DELETE RESTRICT
) STRICT;

CREATE TRIGGER trg_project_display_names_delete
BEFORE DELETE ON project_display_names
BEGIN
    SELECT RAISE(ABORT, 'project display names cannot be removed');
END;
