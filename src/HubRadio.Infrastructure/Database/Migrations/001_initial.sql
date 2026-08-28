CREATE TABLE schema_migrations (
    version INTEGER NOT NULL PRIMARY KEY CHECK (version > 0),
    name TEXT NOT NULL CHECK (length(name) > 0 AND trim(name) = name),
    content_digest TEXT NOT NULL CHECK (
        length(content_digest) = 64
        AND content_digest = lower(content_digest)
        AND content_digest NOT GLOB '*[^0-9a-f]*'
    ),
    applied_at TEXT NOT NULL CHECK (
        length(applied_at) > 0
        AND trim(applied_at) = applied_at
    )
) STRICT;

CREATE TABLE projects (
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
    publication_scope TEXT NOT NULL CHECK (
        length(publication_scope) > 0
        AND trim(publication_scope) = publication_scope
    ),
    deadline TEXT CHECK (
        deadline IS NULL
        OR (length(deadline) > 0 AND trim(deadline) = deadline)
    ),
    payload BLOB NOT NULL CHECK (length(payload) > 0),
    updated_at TEXT NOT NULL CHECK (
        length(updated_at) > 0
        AND trim(updated_at) = updated_at
    )
) STRICT;

CREATE TABLE composition_versions (
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
    project_id TEXT NOT NULL CHECK (
        length(project_id) = 36
        AND project_id = lower(project_id)
        AND substr(project_id, 9, 1) = '-'
        AND substr(project_id, 14, 1) = '-'
        AND substr(project_id, 19, 1) = '-'
        AND substr(project_id, 24, 1) = '-'
        AND length(replace(project_id, '-', '')) = 32
        AND replace(project_id, '-', '') NOT GLOB '*[^0-9a-f]*'
        AND project_id <> '00000000-0000-0000-0000-000000000000'
    ),
    parent_version_id TEXT CHECK (
        parent_version_id IS NULL
        OR (
            length(parent_version_id) = 36
            AND parent_version_id = lower(parent_version_id)
            AND substr(parent_version_id, 9, 1) = '-'
            AND substr(parent_version_id, 14, 1) = '-'
            AND substr(parent_version_id, 19, 1) = '-'
            AND substr(parent_version_id, 24, 1) = '-'
            AND length(replace(parent_version_id, '-', '')) = 32
            AND replace(parent_version_id, '-', '') NOT GLOB '*[^0-9a-f]*'
            AND parent_version_id <> '00000000-0000-0000-0000-000000000000'
        )
    ),
    content_digest TEXT NOT NULL CHECK (
        length(content_digest) = 64
        AND content_digest = lower(content_digest)
        AND content_digest NOT GLOB '*[^0-9a-f]*'
    ),
    created_at TEXT NOT NULL CHECK (
        length(created_at) > 0
        AND trim(created_at) = created_at
    ),
    payload BLOB NOT NULL CHECK (length(payload) > 0),
    UNIQUE (id, project_id),
    UNIQUE (id, project_id, content_digest),
    CHECK (parent_version_id IS NULL OR parent_version_id <> id),
    FOREIGN KEY (project_id) REFERENCES projects (id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    FOREIGN KEY (parent_version_id, project_id)
        REFERENCES composition_versions (id, project_id)
        ON UPDATE RESTRICT ON DELETE RESTRICT
) STRICT;

CREATE INDEX idx_composition_versions_project_id
    ON composition_versions (project_id);
CREATE INDEX idx_composition_versions_content_digest
    ON composition_versions (content_digest);

CREATE TABLE working_copies (
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
    project_id TEXT NOT NULL CHECK (
        length(project_id) = 36
        AND project_id = lower(project_id)
        AND substr(project_id, 9, 1) = '-'
        AND substr(project_id, 14, 1) = '-'
        AND substr(project_id, 19, 1) = '-'
        AND substr(project_id, 24, 1) = '-'
        AND length(replace(project_id, '-', '')) = 32
        AND replace(project_id, '-', '') NOT GLOB '*[^0-9a-f]*'
        AND project_id <> '00000000-0000-0000-0000-000000000000'
    ),
    base_version_id TEXT CHECK (
        base_version_id IS NULL
        OR (
            length(base_version_id) = 36
            AND base_version_id = lower(base_version_id)
            AND substr(base_version_id, 9, 1) = '-'
            AND substr(base_version_id, 14, 1) = '-'
            AND substr(base_version_id, 19, 1) = '-'
            AND substr(base_version_id, 24, 1) = '-'
            AND length(replace(base_version_id, '-', '')) = 32
            AND replace(base_version_id, '-', '') NOT GLOB '*[^0-9a-f]*'
            AND base_version_id <> '00000000-0000-0000-0000-000000000000'
        )
    ),
    revision INTEGER NOT NULL CHECK (revision >= 0),
    updated_at TEXT NOT NULL CHECK (
        length(updated_at) > 0
        AND trim(updated_at) = updated_at
    ),
    payload BLOB NOT NULL CHECK (length(payload) > 0),
    UNIQUE (project_id),
    FOREIGN KEY (project_id) REFERENCES projects (id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    FOREIGN KEY (base_version_id, project_id)
        REFERENCES composition_versions (id, project_id)
        ON UPDATE RESTRICT ON DELETE RESTRICT
) STRICT;

CREATE TABLE review_records (
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
    project_id TEXT NOT NULL CHECK (
        length(project_id) = 36
        AND project_id = lower(project_id)
        AND substr(project_id, 9, 1) = '-'
        AND substr(project_id, 14, 1) = '-'
        AND substr(project_id, 19, 1) = '-'
        AND substr(project_id, 24, 1) = '-'
        AND length(replace(project_id, '-', '')) = 32
        AND replace(project_id, '-', '') NOT GLOB '*[^0-9a-f]*'
        AND project_id <> '00000000-0000-0000-0000-000000000000'
    ),
    composition_version_id TEXT NOT NULL CHECK (
        length(composition_version_id) = 36
        AND composition_version_id = lower(composition_version_id)
        AND substr(composition_version_id, 9, 1) = '-'
        AND substr(composition_version_id, 14, 1) = '-'
        AND substr(composition_version_id, 19, 1) = '-'
        AND substr(composition_version_id, 24, 1) = '-'
        AND length(replace(composition_version_id, '-', '')) = 32
        AND replace(composition_version_id, '-', '') NOT GLOB '*[^0-9a-f]*'
        AND composition_version_id <> '00000000-0000-0000-0000-000000000000'
    ),
    composition_version_digest TEXT NOT NULL CHECK (
        length(composition_version_digest) = 64
        AND composition_version_digest = lower(composition_version_digest)
        AND composition_version_digest NOT GLOB '*[^0-9a-f]*'
    ),
    state TEXT NOT NULL CHECK (length(state) > 0 AND trim(state) = state),
    actor_id TEXT NOT NULL CHECK (length(actor_id) > 0 AND trim(actor_id) = actor_id),
    actor_role TEXT NOT NULL CHECK (
        length(actor_role) > 0
        AND trim(actor_role) = actor_role
    ),
    recorded_at TEXT NOT NULL CHECK (
        length(recorded_at) > 0
        AND trim(recorded_at) = recorded_at
    ),
    reason TEXT NOT NULL CHECK (length(reason) > 0 AND trim(reason) = reason),
    FOREIGN KEY (project_id) REFERENCES projects (id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    FOREIGN KEY (composition_version_id, project_id, composition_version_digest)
        REFERENCES composition_versions (id, project_id, content_digest)
        ON UPDATE RESTRICT ON DELETE RESTRICT
) STRICT;

CREATE INDEX idx_review_records_project_id
    ON review_records (project_id);
CREATE INDEX idx_review_records_version
    ON review_records (
        composition_version_id,
        project_id,
        composition_version_digest
    );

CREATE TABLE review_comments (
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
    project_id TEXT NOT NULL CHECK (
        length(project_id) = 36
        AND project_id = lower(project_id)
        AND substr(project_id, 9, 1) = '-'
        AND substr(project_id, 14, 1) = '-'
        AND substr(project_id, 19, 1) = '-'
        AND substr(project_id, 24, 1) = '-'
        AND length(replace(project_id, '-', '')) = 32
        AND replace(project_id, '-', '') NOT GLOB '*[^0-9a-f]*'
        AND project_id <> '00000000-0000-0000-0000-000000000000'
    ),
    composition_version_id TEXT NOT NULL CHECK (
        length(composition_version_id) = 36
        AND composition_version_id = lower(composition_version_id)
        AND substr(composition_version_id, 9, 1) = '-'
        AND substr(composition_version_id, 14, 1) = '-'
        AND substr(composition_version_id, 19, 1) = '-'
        AND substr(composition_version_id, 24, 1) = '-'
        AND length(replace(composition_version_id, '-', '')) = 32
        AND replace(composition_version_id, '-', '') NOT GLOB '*[^0-9a-f]*'
        AND composition_version_id <> '00000000-0000-0000-0000-000000000000'
    ),
    composition_version_digest TEXT NOT NULL CHECK (
        length(composition_version_digest) = 64
        AND composition_version_digest = lower(composition_version_digest)
        AND composition_version_digest NOT GLOB '*[^0-9a-f]*'
    ),
    output_value INTEGER NOT NULL CHECK (output_value >= 0),
    output_rate INTEGER NOT NULL CHECK (output_rate > 0),
    source_span_id TEXT NOT NULL CHECK (
        length(source_span_id) = 36
        AND source_span_id = lower(source_span_id)
        AND substr(source_span_id, 9, 1) = '-'
        AND substr(source_span_id, 14, 1) = '-'
        AND substr(source_span_id, 19, 1) = '-'
        AND substr(source_span_id, 24, 1) = '-'
        AND length(replace(source_span_id, '-', '')) = 32
        AND replace(source_span_id, '-', '') NOT GLOB '*[^0-9a-f]*'
        AND source_span_id <> '00000000-0000-0000-0000-000000000000'
    ),
    actor_id TEXT NOT NULL CHECK (length(actor_id) > 0 AND trim(actor_id) = actor_id),
    actor_role TEXT NOT NULL CHECK (
        length(actor_role) > 0
        AND trim(actor_role) = actor_role
    ),
    created_at TEXT NOT NULL CHECK (
        length(created_at) > 0
        AND trim(created_at) = created_at
    ),
    text TEXT NOT NULL CHECK (length(text) > 0 AND trim(text) = text),
    FOREIGN KEY (project_id) REFERENCES projects (id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    FOREIGN KEY (composition_version_id, project_id, composition_version_digest)
        REFERENCES composition_versions (id, project_id, content_digest)
        ON UPDATE RESTRICT ON DELETE RESTRICT
) STRICT;

CREATE INDEX idx_review_comments_project_id
    ON review_comments (project_id);
CREATE INDEX idx_review_comments_version
    ON review_comments (
        composition_version_id,
        project_id,
        composition_version_digest
    );

CREATE TABLE approvals (
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
    project_id TEXT NOT NULL CHECK (
        length(project_id) = 36
        AND project_id = lower(project_id)
        AND substr(project_id, 9, 1) = '-'
        AND substr(project_id, 14, 1) = '-'
        AND substr(project_id, 19, 1) = '-'
        AND substr(project_id, 24, 1) = '-'
        AND length(replace(project_id, '-', '')) = 32
        AND replace(project_id, '-', '') NOT GLOB '*[^0-9a-f]*'
        AND project_id <> '00000000-0000-0000-0000-000000000000'
    ),
    composition_version_id TEXT NOT NULL CHECK (
        length(composition_version_id) = 36
        AND composition_version_id = lower(composition_version_id)
        AND substr(composition_version_id, 9, 1) = '-'
        AND substr(composition_version_id, 14, 1) = '-'
        AND substr(composition_version_id, 19, 1) = '-'
        AND substr(composition_version_id, 24, 1) = '-'
        AND length(replace(composition_version_id, '-', '')) = 32
        AND replace(composition_version_id, '-', '') NOT GLOB '*[^0-9a-f]*'
        AND composition_version_id <> '00000000-0000-0000-0000-000000000000'
    ),
    composition_version_digest TEXT NOT NULL CHECK (
        length(composition_version_digest) = 64
        AND composition_version_digest = lower(composition_version_digest)
        AND composition_version_digest NOT GLOB '*[^0-9a-f]*'
    ),
    actor_id TEXT NOT NULL CHECK (length(actor_id) > 0 AND trim(actor_id) = actor_id),
    actor_role TEXT NOT NULL CHECK (
        length(actor_role) > 0
        AND trim(actor_role) = actor_role
    ),
    approved_at TEXT NOT NULL CHECK (
        length(approved_at) > 0
        AND trim(approved_at) = approved_at
    ),
    reason TEXT NOT NULL CHECK (length(reason) > 0 AND trim(reason) = reason),
    FOREIGN KEY (project_id) REFERENCES projects (id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    FOREIGN KEY (composition_version_id, project_id, composition_version_digest)
        REFERENCES composition_versions (id, project_id, content_digest)
        ON UPDATE RESTRICT ON DELETE RESTRICT
) STRICT;

CREATE INDEX idx_approvals_project_id
    ON approvals (project_id);
CREATE INDEX idx_approvals_version
    ON approvals (
        composition_version_id,
        project_id,
        composition_version_digest
    );

CREATE TABLE audit_events (
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
    project_id TEXT NOT NULL CHECK (
        length(project_id) = 36
        AND project_id = lower(project_id)
        AND substr(project_id, 9, 1) = '-'
        AND substr(project_id, 14, 1) = '-'
        AND substr(project_id, 19, 1) = '-'
        AND substr(project_id, 24, 1) = '-'
        AND length(replace(project_id, '-', '')) = 32
        AND replace(project_id, '-', '') NOT GLOB '*[^0-9a-f]*'
        AND project_id <> '00000000-0000-0000-0000-000000000000'
    ),
    kind TEXT NOT NULL CHECK (length(kind) > 0 AND trim(kind) = kind),
    actor_id TEXT NOT NULL CHECK (length(actor_id) > 0 AND trim(actor_id) = actor_id),
    actor_role TEXT NOT NULL CHECK (
        length(actor_role) > 0
        AND trim(actor_role) = actor_role
    ),
    occurred_at TEXT NOT NULL CHECK (
        length(occurred_at) > 0
        AND trim(occurred_at) = occurred_at
    ),
    reason TEXT NOT NULL CHECK (length(reason) > 0 AND trim(reason) = reason),
    composition_version_id TEXT CHECK (
        composition_version_id IS NULL
        OR (
            length(composition_version_id) = 36
            AND composition_version_id = lower(composition_version_id)
            AND substr(composition_version_id, 9, 1) = '-'
            AND substr(composition_version_id, 14, 1) = '-'
            AND substr(composition_version_id, 19, 1) = '-'
            AND substr(composition_version_id, 24, 1) = '-'
            AND length(replace(composition_version_id, '-', '')) = 32
            AND replace(composition_version_id, '-', '') NOT GLOB '*[^0-9a-f]*'
            AND composition_version_id <> '00000000-0000-0000-0000-000000000000'
        )
    ),
    composition_version_digest TEXT CHECK (
        composition_version_digest IS NULL
        OR (
            length(composition_version_digest) = 64
            AND composition_version_digest = lower(composition_version_digest)
            AND composition_version_digest NOT GLOB '*[^0-9a-f]*'
        )
    ),
    CHECK (
        (composition_version_id IS NULL AND composition_version_digest IS NULL)
        OR (composition_version_id IS NOT NULL AND composition_version_digest IS NOT NULL)
    ),
    FOREIGN KEY (project_id) REFERENCES projects (id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    FOREIGN KEY (composition_version_id, project_id, composition_version_digest)
        REFERENCES composition_versions (id, project_id, content_digest)
        ON UPDATE RESTRICT ON DELETE RESTRICT
) STRICT;

CREATE INDEX idx_audit_events_project_occurred_at
    ON audit_events (project_id, occurred_at);

CREATE TABLE artifacts (
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
    project_id TEXT NOT NULL CHECK (
        length(project_id) = 36
        AND project_id = lower(project_id)
        AND substr(project_id, 9, 1) = '-'
        AND substr(project_id, 14, 1) = '-'
        AND substr(project_id, 19, 1) = '-'
        AND substr(project_id, 24, 1) = '-'
        AND length(replace(project_id, '-', '')) = 32
        AND replace(project_id, '-', '') NOT GLOB '*[^0-9a-f]*'
        AND project_id <> '00000000-0000-0000-0000-000000000000'
    ),
    kind TEXT NOT NULL CHECK (length(kind) > 0 AND trim(kind) = kind),
    storage_reference TEXT NOT NULL CHECK (
        length(storage_reference) > 0
        AND trim(storage_reference) = storage_reference
    ),
    content_digest TEXT NOT NULL CHECK (
        length(content_digest) = 64
        AND content_digest = lower(content_digest)
        AND content_digest NOT GLOB '*[^0-9a-f]*'
    ),
    byte_size INTEGER NOT NULL CHECK (byte_size > 0),
    created_at TEXT NOT NULL CHECK (
        length(created_at) > 0
        AND trim(created_at) = created_at
    ),
    UNIQUE (project_id, storage_reference),
    FOREIGN KEY (project_id) REFERENCES projects (id)
        ON UPDATE RESTRICT ON DELETE RESTRICT
) STRICT;

CREATE INDEX idx_artifacts_content_digest
    ON artifacts (content_digest);
