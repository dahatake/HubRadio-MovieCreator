-- No existing composition is inferred to be a subtitle mutation.
-- The native caller proves the generated root/input and the first subtitle snapshot.
-- Subsequent snapshots and record digests form one immutable chain per root.
CREATE TABLE subtitle_edits (
    request_id TEXT NOT NULL PRIMARY KEY CHECK (
        length(request_id) = 36 AND request_id = lower(request_id)
        AND substr(request_id, 9, 1) = '-' AND substr(request_id, 14, 1) = '-'
        AND substr(request_id, 19, 1) = '-' AND substr(request_id, 24, 1) = '-'
        AND length(replace(request_id, '-', '')) = 32
        AND replace(request_id, '-', '') NOT GLOB '*[^0-9a-f]*'
        AND request_id <> '00000000-0000-0000-0000-000000000000'
    ),
    project_id TEXT NOT NULL,
    root_version_id TEXT NOT NULL,
    parent_version_id TEXT NOT NULL,
    child_version_id TEXT NOT NULL UNIQUE,
    sequence INTEGER NOT NULL CHECK (sequence BETWEEN 1 AND 2147483647),
    generated_input_digest TEXT NOT NULL CHECK (length(generated_input_digest) = 64 AND generated_input_digest NOT GLOB '*[^0-9a-f]*'),
    parent_snapshot_digest TEXT NOT NULL CHECK (length(parent_snapshot_digest) = 64 AND parent_snapshot_digest NOT GLOB '*[^0-9a-f]*'),
    result_snapshot_digest TEXT NOT NULL CHECK (length(result_snapshot_digest) = 64 AND result_snapshot_digest NOT GLOB '*[^0-9a-f]*'),
    child_content_digest TEXT NOT NULL CHECK (length(child_content_digest) = 64 AND child_content_digest NOT GLOB '*[^0-9a-f]*'),
    root_payload_digest TEXT NOT NULL CHECK (length(root_payload_digest) = 64 AND root_payload_digest NOT GLOB '*[^0-9a-f]*'),
    child_payload_digest TEXT NOT NULL CHECK (length(child_payload_digest) = 64 AND child_payload_digest NOT GLOB '*[^0-9a-f]*'),
    actor_sid TEXT NOT NULL CHECK (length(actor_sid) BETWEEN 5 AND 256 AND trim(actor_sid) = actor_sid),
    occurred_at TEXT NOT NULL CHECK (length(occurred_at) = 33 AND substr(occurred_at, 28) = '+00:00'),
    -- Complete canonical commit: original operation, actual IDs/time and child JSON.
    commit_payload BLOB NOT NULL CHECK (length(commit_payload) BETWEEN 1 AND 5242880),
    commit_digest TEXT NOT NULL CHECK (length(commit_digest) = 64 AND commit_digest NOT GLOB '*[^0-9a-f]*'),
    previous_record_digest TEXT CHECK (previous_record_digest IS NULL OR
        (length(previous_record_digest) = 64 AND previous_record_digest NOT GLOB '*[^0-9a-f]*')),
    record_digest TEXT NOT NULL CHECK (length(record_digest) = 64 AND record_digest NOT GLOB '*[^0-9a-f]*'),
    UNIQUE (project_id, root_version_id, sequence),
    CHECK (root_version_id <> child_version_id AND parent_version_id <> child_version_id),
    CHECK (parent_snapshot_digest <> result_snapshot_digest),
    CHECK ((sequence = 1) = (previous_record_digest IS NULL)),
    FOREIGN KEY (project_id) REFERENCES projects(id) ON UPDATE RESTRICT ON DELETE RESTRICT,
    FOREIGN KEY (root_version_id, project_id)
        REFERENCES composition_versions(id, project_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
    FOREIGN KEY (parent_version_id, project_id)
        REFERENCES composition_versions(id, project_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
    FOREIGN KEY (child_version_id, project_id, child_content_digest)
        REFERENCES composition_versions(id, project_id, content_digest) ON UPDATE RESTRICT ON DELETE RESTRICT
) STRICT;

CREATE TRIGGER trg_subtitle_edits_insert
BEFORE INSERT ON subtitle_edits
WHEN
    -- REPLACE can bypass DELETE triggers with recursive_triggers OFF. Reject the
    -- collisions before SQLite removes any old row (including branch/child keys).
    EXISTS (SELECT 1 FROM subtitle_edits WHERE request_id = NEW.request_id OR child_version_id = NEW.child_version_id
        OR (project_id = NEW.project_id AND root_version_id = NEW.root_version_id AND sequence = NEW.sequence))
    OR EXISTS (SELECT 1 FROM subtitle_edits WHERE child_version_id = NEW.root_version_id)
    OR NEW.sequence <> (SELECT coalesce(max(sequence), 0) + 1 FROM subtitle_edits
        WHERE project_id = NEW.project_id AND root_version_id = NEW.root_version_id)
    OR (NEW.sequence = 1 AND (NEW.parent_version_id <> NEW.root_version_id OR NEW.previous_record_digest IS NOT NULL))
    OR (NEW.sequence > 1 AND NOT EXISTS (
        SELECT 1 FROM subtitle_edits previous
        WHERE previous.project_id = NEW.project_id AND previous.root_version_id = NEW.root_version_id
            AND previous.sequence = NEW.sequence - 1 AND previous.child_version_id = NEW.parent_version_id
            AND previous.record_digest = NEW.previous_record_digest
            AND previous.result_snapshot_digest = NEW.parent_snapshot_digest
            AND previous.generated_input_digest = NEW.generated_input_digest
            AND previous.root_payload_digest = NEW.root_payload_digest
            AND previous.occurred_at <= NEW.occurred_at))
    OR NOT EXISTS (
        SELECT 1 FROM composition_versions child JOIN composition_versions parent ON parent.id = NEW.parent_version_id
        WHERE child.id = NEW.child_version_id AND child.project_id = NEW.project_id AND parent.project_id = NEW.project_id
            AND child.parent_version_id = parent.id AND child.content_digest = NEW.child_content_digest
            AND child.content_digest = parent.content_digest AND child.created_at = NEW.occurred_at
            AND parent.created_at <= NEW.occurred_at
            AND json_extract(CAST(child.payload AS TEXT), '$.createdByActorId') IS NEW.actor_sid
            AND json_extract(CAST(child.payload AS TEXT), '$.elements')
                IS json_extract(CAST(parent.payload AS TEXT), '$.elements'))
    -- Typed domain validation and cryptographic recomputation are performed by the
    -- store before AND after insert. These guards bind the duplicated SQL headers.
    OR json_extract(CAST(NEW.commit_payload AS TEXT), '$.schema') IS NOT 'subtitle-edit.commit.v1'
    OR json_extract(CAST(NEW.commit_payload AS TEXT), '$.requestId') IS NOT NEW.request_id
    OR json_extract(CAST(NEW.commit_payload AS TEXT), '$.projectId') IS NOT NEW.project_id
    OR json_extract(CAST(NEW.commit_payload AS TEXT), '$.rootVersionId') IS NOT NEW.root_version_id
    OR json_extract(CAST(NEW.commit_payload AS TEXT), '$.parentVersionId') IS NOT NEW.parent_version_id
    OR json_extract(CAST(NEW.commit_payload AS TEXT), '$.childVersion.id') IS NOT NEW.child_version_id
    OR json_extract(CAST(NEW.commit_payload AS TEXT), '$.childVersion.contentDigest') IS NOT NEW.child_content_digest
    OR json_extract(CAST(NEW.commit_payload AS TEXT), '$.expectedSequence') IS NOT NEW.sequence - 1
    OR json_extract(CAST(NEW.commit_payload AS TEXT), '$.generatedInputDigest') IS NOT NEW.generated_input_digest
    OR json_extract(CAST(NEW.commit_payload AS TEXT), '$.parentSnapshotDigest') IS NOT NEW.parent_snapshot_digest
    OR json_extract(CAST(NEW.commit_payload AS TEXT), '$.resultSnapshotDigest') IS NOT NEW.result_snapshot_digest
    OR json_extract(CAST(NEW.commit_payload AS TEXT), '$.actorSid') IS NOT NEW.actor_sid
    OR json_extract(CAST(NEW.commit_payload AS TEXT), '$.occurredAt') IS NOT NEW.occurred_at
    OR json_extract(CAST(NEW.commit_payload AS TEXT), '$.operation.kind') IS NOT 'editSubtitle'
    OR json_type(CAST(NEW.commit_payload AS TEXT), '$.allocatedIds') IS NOT 'array'
    OR json_array_length(CAST(NEW.commit_payload AS TEXT), '$.allocatedIds') NOT BETWEEN 1 AND 8
    OR json_extract(CAST(NEW.commit_payload AS TEXT), '$.allocatedIds[0]') IS NOT NEW.child_version_id
BEGIN
    SELECT RAISE(ABORT, 'invalid subtitle edit branch or immutable identity');
END;

CREATE TRIGGER trg_subtitle_edits_update
BEFORE UPDATE ON subtitle_edits
BEGIN
    SELECT RAISE(ABORT, 'subtitle edit records are immutable');
END;

CREATE TRIGGER trg_subtitle_edits_delete
BEFORE DELETE ON subtitle_edits
BEGIN
    SELECT RAISE(ABORT, 'subtitle edit records cannot be deleted');
END;

-- Protect every bound version, including the generated root, without changing
-- mutability of unrelated versions owned by the other store workflows.
CREATE TRIGGER trg_subtitle_edits_version_insert
BEFORE INSERT ON composition_versions
WHEN EXISTS (SELECT 1 FROM subtitle_edits
    WHERE root_version_id = NEW.id OR parent_version_id = NEW.id OR child_version_id = NEW.id)
BEGIN
    SELECT RAISE(ABORT, 'subtitle-bound versions cannot be replaced');
END;

CREATE TRIGGER trg_subtitle_edits_version_update
BEFORE UPDATE ON composition_versions
WHEN EXISTS (SELECT 1 FROM subtitle_edits
    WHERE root_version_id IN (OLD.id, NEW.id) OR parent_version_id IN (OLD.id, NEW.id) OR child_version_id IN (OLD.id, NEW.id))
BEGIN
    SELECT RAISE(ABORT, 'subtitle-bound versions are immutable');
END;

CREATE TRIGGER trg_subtitle_edits_version_delete
BEFORE DELETE ON composition_versions
WHEN EXISTS (SELECT 1 FROM subtitle_edits
    WHERE root_version_id = OLD.id OR parent_version_id = OLD.id OR child_version_id = OLD.id)
BEGIN
    SELECT RAISE(ABORT, 'subtitle-bound versions cannot be deleted');
END;