-- No legacy row is inferred to be an accepted production execution.
CREATE TABLE source_only_executions (
    id TEXT NOT NULL PRIMARY KEY CHECK (
        length(id) = 36 AND id = lower(id)
        AND substr(id, 9, 1) = '-' AND substr(id, 14, 1) = '-'
        AND substr(id, 19, 1) = '-' AND substr(id, 24, 1) = '-'
        AND length(replace(id, '-', '')) = 32
        AND replace(id, '-', '') NOT GLOB '*[^0-9a-f]*'
        AND id <> '00000000-0000-0000-0000-000000000000'
    ),
    project_id TEXT NOT NULL,
    draft_id TEXT NOT NULL,
    source_candidate_id TEXT NOT NULL CHECK (
        length(source_candidate_id) = 36 AND source_candidate_id = lower(source_candidate_id)
        AND substr(source_candidate_id, 9, 1) = '-' AND substr(source_candidate_id, 14, 1) = '-'
        AND substr(source_candidate_id, 19, 1) = '-' AND substr(source_candidate_id, 24, 1) = '-'
        AND length(replace(source_candidate_id, '-', '')) = 32
        AND replace(source_candidate_id, '-', '') NOT GLOB '*[^0-9a-f]*'
        AND source_candidate_id <> '00000000-0000-0000-0000-000000000000'
    ),
    root_job_id TEXT NOT NULL UNIQUE,
    ingest_job_id TEXT NOT NULL,
    parent_version_id TEXT,
    parent_digest TEXT,
    created_at TEXT NOT NULL CHECK (length(created_at) = 33 AND substr(created_at, 28) = '+00:00'),
    request_payload BLOB NOT NULL CHECK (length(request_payload) BETWEEN 1 AND 2097152),
    request_digest TEXT NOT NULL CHECK (length(request_digest) = 64 AND request_digest NOT GLOB '*[^0-9a-f]*'),
    protected_source_locator BLOB CHECK (protected_source_locator IS NULL OR length(protected_source_locator) BETWEEN 1 AND 65536),
    revision INTEGER NOT NULL CHECK (revision BETWEEN 0 AND 2147483647),
    phase TEXT NOT NULL CHECK (phase IN (
        'Queued', 'Ingesting', 'Analyzing', 'Composing', 'Rendering', 'Evaluating',
        'Delivering', 'Completed', 'Failed', 'Canceled', 'ReselectionRequired', 'ReconciliationRequired'
    )),
    result_payload BLOB CHECK (result_payload IS NULL OR length(result_payload) BETWEEN 1 AND 4194304),
    result_digest TEXT CHECK (result_digest IS NULL OR (length(result_digest) = 64 AND result_digest NOT GLOB '*[^0-9a-f]*')),
    version_id TEXT,
    version_digest TEXT,
    failure_code TEXT CHECK (failure_code IS NULL OR (
        length(failure_code) BETWEEN 1 AND 128 AND failure_code NOT GLOB '*[^a-z0-9_.-]*'
    )),
    state_digest TEXT NOT NULL CHECK (length(state_digest) = 64 AND state_digest NOT GLOB '*[^0-9a-f]*'),
    retry_of_execution_id TEXT CHECK (retry_of_execution_id IS NULL OR retry_of_execution_id <> id),
    CHECK (root_job_id <> ingest_job_id),
    CHECK ((parent_version_id IS NULL) = (parent_digest IS NULL)),
    CHECK ((version_id IS NULL) = (version_digest IS NULL)),
    CHECK ((result_payload IS NULL) = (result_digest IS NULL)),
    CHECK (result_payload IS NULL OR version_id IS NOT NULL),
    CHECK (phase <> 'Completed' OR (version_id IS NOT NULL AND result_payload IS NOT NULL AND failure_code IS NULL)),
    CHECK ((phase IN ('Failed', 'ReselectionRequired', 'ReconciliationRequired')) = (failure_code IS NOT NULL)),
    FOREIGN KEY (project_id) REFERENCES projects(id) ON UPDATE RESTRICT ON DELETE RESTRICT,
    FOREIGN KEY (draft_id) REFERENCES project_creation_drafts(id) ON UPDATE RESTRICT ON DELETE RESTRICT,
    FOREIGN KEY (root_job_id, project_id) REFERENCES jobs(id, project_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
    FOREIGN KEY (ingest_job_id, project_id) REFERENCES jobs(id, project_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
    FOREIGN KEY (parent_version_id, project_id, parent_digest)
        REFERENCES composition_versions(id, project_id, content_digest) ON UPDATE RESTRICT ON DELETE RESTRICT,
    FOREIGN KEY (version_id, project_id, version_digest)
        REFERENCES composition_versions(id, project_id, content_digest) ON UPDATE RESTRICT ON DELETE RESTRICT,
    FOREIGN KEY (retry_of_execution_id) REFERENCES source_only_executions(id) ON UPDATE RESTRICT ON DELETE RESTRICT
) STRICT;

CREATE UNIQUE INDEX idx_source_only_executions_initial_draft
    ON source_only_executions(draft_id) WHERE parent_version_id IS NULL AND retry_of_execution_id IS NULL;
-- One successor per terminal acceptance: retrying a retry follows that successor,
-- never a second branch from the original. Original draft/root uniqueness is retained.
CREATE UNIQUE INDEX idx_source_only_executions_retry
    ON source_only_executions(retry_of_execution_id) WHERE retry_of_execution_id IS NOT NULL;
CREATE UNIQUE INDEX idx_source_only_executions_result_version
    ON source_only_executions(version_id) WHERE version_id IS NOT NULL;
CREATE INDEX idx_source_only_executions_latest
    ON source_only_executions(project_id, created_at DESC, id DESC);
CREATE INDEX idx_source_only_executions_ingest
    ON source_only_executions(ingest_job_id, created_at DESC, id DESC);

CREATE TRIGGER trg_source_only_execution_insert
BEFORE INSERT ON source_only_executions
WHEN NEW.revision <> 0 OR NEW.phase <> 'Queued' OR NEW.result_payload IS NOT NULL
    OR NEW.version_id IS NOT NULL OR NEW.failure_code IS NOT NULL
    -- REPLACE deletes conflicting rows without DELETE triggers when recursive_triggers
    -- is OFF. BEFORE INSERT still sees them; reject all acceptance identities here.
    OR EXISTS (SELECT 1 FROM source_only_executions WHERE id = NEW.id OR root_job_id = NEW.root_job_id
        OR (NEW.parent_version_id IS NULL AND NEW.retry_of_execution_id IS NULL
            AND parent_version_id IS NULL AND retry_of_execution_id IS NULL AND draft_id = NEW.draft_id)
        OR (NEW.retry_of_execution_id IS NOT NULL AND retry_of_execution_id = NEW.retry_of_execution_id))
    OR NOT EXISTS (SELECT 1 FROM jobs WHERE id = NEW.root_job_id
        AND project_id = NEW.project_id AND kind = 'source-only.pipeline.v1'
        AND state = 'queued' AND current_attempt_number = 0 AND revision = 0
        AND created_at = NEW.created_at AND updated_at = NEW.created_at)
    OR NOT EXISTS (SELECT 1 FROM jobs j WHERE j.id = NEW.ingest_job_id
        AND j.project_id = NEW.project_id AND j.kind = 'media.ingest.v1'
        AND (NEW.parent_version_id IS NULL OR j.state = 'succeeded')
        AND ((NEW.retry_of_execution_id IS NULL
                AND ((NEW.parent_version_id IS NULL AND j.state = 'queued' AND j.current_attempt_number = 0
                        AND j.revision = 0 AND j.created_at = NEW.created_at AND j.updated_at = NEW.created_at)
                    OR (NEW.parent_version_id IS NOT NULL AND j.state = 'succeeded')))
            OR (NEW.retry_of_execution_id IS NOT NULL
                AND ((j.state = 'queued' AND j.current_attempt_number = 0 AND j.revision = 0
                        AND j.created_at = NEW.created_at AND j.updated_at = NEW.created_at)
                    OR EXISTS (SELECT 1 FROM source_only_executions previous
                        WHERE previous.id = NEW.retry_of_execution_id AND previous.ingest_job_id = j.id
                            AND (j.state = 'succeeded' OR (j.state = 'queued' AND j.current_attempt_number = 0 AND j.revision = 0)))))))
    OR (NEW.retry_of_execution_id IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM source_only_executions previous
        JOIN jobs old_root ON old_root.id = previous.root_job_id
        JOIN jobs old_ingest ON old_ingest.id = previous.ingest_job_id
        WHERE previous.id = NEW.retry_of_execution_id
            AND previous.phase IN ('Failed', 'Canceled') AND previous.version_id IS NULL AND previous.result_payload IS NULL
            AND previous.project_id = NEW.project_id AND previous.draft_id = NEW.draft_id
            AND previous.source_candidate_id = NEW.source_candidate_id
            AND previous.parent_version_id IS NEW.parent_version_id AND previous.parent_digest IS NEW.parent_digest
            AND previous.protected_source_locator IS NEW.protected_source_locator AND previous.created_at < NEW.created_at
            AND old_root.state IN ('failed', 'cancelled', 'interrupted') AND old_root.updated_at <= NEW.created_at
            AND old_ingest.updated_at <= NEW.created_at
            AND ((previous.ingest_job_id = NEW.ingest_job_id
                    AND (old_ingest.state = 'succeeded' OR (old_ingest.state = 'queued'
                        AND old_ingest.current_attempt_number = 0 AND old_ingest.revision = 0)))
                OR (previous.ingest_job_id <> NEW.ingest_job_id AND old_ingest.state IN ('failed', 'cancelled', 'interrupted')))
            AND json_extract(CAST(NEW.request_payload AS TEXT), '$.retryOfExecutionId') IS previous.id
            AND json_extract(CAST(NEW.request_payload AS TEXT), '$.retryOfRevision') IS previous.revision
            AND json_extract(CAST(NEW.request_payload AS TEXT), '$.retryOfStateDigest') IS previous.state_digest
            AND json_extract(CAST(NEW.request_payload AS TEXT), '$.settingsSnapshot')
                IS json_extract(CAST(previous.request_payload AS TEXT), '$.settingsSnapshot')
            AND json_extract(CAST(NEW.request_payload AS TEXT), '$.settingsRevision')
                IS json_extract(CAST(previous.request_payload AS TEXT), '$.settingsRevision')
            AND json_extract(CAST(NEW.request_payload AS TEXT), '$.finalizationDigest')
                IS json_extract(CAST(previous.request_payload AS TEXT), '$.finalizationDigest')
    ))
    OR (NEW.retry_of_execution_id IS NOT NULL AND NOT EXISTS (
        -- Including NEW, at most MaximumRetryLineageLength (64) records.
        WITH RECURSIVE prior(id, retry_of_execution_id, depth) AS (
            SELECT id, retry_of_execution_id, 1 FROM source_only_executions WHERE id = NEW.retry_of_execution_id
            UNION ALL
            SELECT e.id, e.retry_of_execution_id, p.depth + 1
            FROM prior p JOIN source_only_executions e ON e.id = p.retry_of_execution_id
            WHERE p.depth < 63
        )
        SELECT 1 FROM prior WHERE retry_of_execution_id IS NULL
    ))
    OR (NEW.parent_version_id IS NOT NULL AND NOT EXISTS (
        -- Reserve at least one of the 256 lineage nodes for a proper result descendant.
        WITH RECURSIVE ancestry(id, parent_id, created_at, depth, path, bad) AS (
            SELECT v.id, v.parent_version_id, v.created_at, 1, '|' || v.id || '|',
                v.project_id IS NOT NEW.project_id OR v.content_digest IS NOT NEW.parent_digest
                    OR v.created_at > NEW.created_at
                    OR length(v.created_at) <> 33 OR substr(v.created_at, 28) <> '+00:00'
            FROM composition_versions v WHERE v.id = NEW.parent_version_id
            UNION ALL
            SELECT p.id, p.parent_version_id, p.created_at, a.depth + 1, a.path || p.id || '|',
                p.project_id IS NOT NEW.project_id OR p.created_at > a.created_at
                    OR length(p.created_at) <> 33 OR substr(p.created_at, 28) <> '+00:00'
                    OR instr(a.path, '|' || p.id || '|') <> 0
            FROM ancestry a JOIN composition_versions p ON p.id = a.parent_id
            WHERE a.depth < 255 AND a.bad = 0
        )
        SELECT 1 FROM ancestry WHERE parent_id IS NULL AND bad = 0
    ))
    OR NOT EXISTS (SELECT 1 FROM project_creation_drafts WHERE id = NEW.draft_id
        AND finalized_project_id = NEW.project_id AND finalized_at IS NOT NULL)
BEGIN
    SELECT RAISE(ABORT, 'invalid source-only acceptance binding');
END;

CREATE TRIGGER trg_source_only_execution_update
BEFORE UPDATE ON source_only_executions
WHEN NEW.id IS NOT OLD.id OR NEW.project_id IS NOT OLD.project_id
    OR NEW.draft_id IS NOT OLD.draft_id OR NEW.source_candidate_id IS NOT OLD.source_candidate_id
    OR NEW.root_job_id IS NOT OLD.root_job_id OR NEW.ingest_job_id IS NOT OLD.ingest_job_id
    OR NEW.parent_version_id IS NOT OLD.parent_version_id OR NEW.parent_digest IS NOT OLD.parent_digest
    OR NEW.created_at IS NOT OLD.created_at OR NEW.request_payload IS NOT OLD.request_payload
    OR NEW.request_digest IS NOT OLD.request_digest OR NEW.protected_source_locator IS NOT OLD.protected_source_locator
    OR NEW.retry_of_execution_id IS NOT OLD.retry_of_execution_id
    OR NEW.revision <> OLD.revision + 1
    OR OLD.phase IN ('Completed', 'Failed', 'Canceled', 'ReselectionRequired', 'ReconciliationRequired')
    OR (OLD.version_id IS NOT NULL AND (NEW.version_id IS NOT OLD.version_id OR NEW.version_digest IS NOT OLD.version_digest))
    OR (NEW.parent_version_id IS NOT NULL AND NEW.phase = 'Ingesting')
    OR (NEW.phase = 'Completed' AND (OLD.phase <> 'Delivering'
        OR NOT EXISTS (SELECT 1 FROM jobs WHERE id = NEW.ingest_job_id AND state = 'succeeded')))
    OR (NEW.phase NOT IN ('Failed', 'Canceled', 'ReselectionRequired', 'ReconciliationRequired', 'Completed')
        AND instr('|Queued|Ingesting|Analyzing|Composing|Rendering|Evaluating|Delivering|', '|' || NEW.phase || '|')
            < instr('|Queued|Ingesting|Analyzing|Composing|Rendering|Evaluating|Delivering|', '|' || OLD.phase || '|'))
    -- A valid result may be baseline -> auto -> standard, not just an immediate child.
    -- 256 includes the result and all ancestors, matching MaximumVersionLineageLength.
    -- CTEs are embedded in subqueries (SQLite does not allow a top-level WITH in a trigger).
    -- Typed elements/evidence digest recomputation remains authoritative in the store.
    OR (NEW.version_id IS NOT NULL AND NOT EXISTS (
        WITH RECURSIVE lineage(id, parent_id, project_id, content_digest, created_at, depth, path, has_parent, bad) AS (
            SELECT v.id, v.parent_version_id, v.project_id, v.content_digest, v.created_at,
                1, '|' || v.id || '|', 0,
                v.project_id IS NOT NEW.project_id OR v.content_digest IS NOT NEW.version_digest
                    OR v.id IS NEW.parent_version_id OR v.created_at < NEW.created_at
                    OR length(v.created_at) <> 33 OR substr(v.created_at, 28) <> '+00:00'
            FROM composition_versions v WHERE v.id = NEW.version_id
            UNION ALL
            SELECT p.id, p.parent_version_id, p.project_id, p.content_digest, p.created_at,
                l.depth + 1, l.path || p.id || '|', l.has_parent OR p.id IS NEW.parent_version_id,
                p.project_id IS NOT NEW.project_id OR p.created_at > l.created_at
                    OR length(p.created_at) <> 33 OR substr(p.created_at, 28) <> '+00:00'
                    OR instr(l.path, '|' || p.id || '|') <> 0
                    OR (l.has_parent = 0 AND p.id IS NOT NEW.parent_version_id AND p.created_at < NEW.created_at)
                    OR (p.id IS NEW.parent_version_id
                        AND (p.content_digest IS NOT NEW.parent_digest OR p.created_at > NEW.created_at))
            FROM lineage l JOIN composition_versions p ON p.id = l.parent_id
            WHERE l.depth < 256 AND l.bad = 0
        )
        SELECT 1 FROM lineage WHERE parent_id IS NULL AND bad = 0
            AND (NEW.parent_version_id IS NULL OR has_parent = 1)
    ))
    OR (NEW.version_id IS NOT NULL AND EXISTS (
        WITH RECURSIVE produced(id, parent_id, depth) AS (
            SELECT id, parent_version_id, 1 FROM composition_versions WHERE id = NEW.version_id
            UNION ALL
            SELECT p.id, p.parent_version_id, c.depth + 1
            FROM produced c JOIN composition_versions p ON p.id = c.parent_id
            WHERE c.depth < 256 AND p.id IS NOT NEW.parent_version_id
        ), claimed(id, stop_id, depth) AS (
            SELECT version_id, parent_version_id, 1 FROM source_only_executions
            WHERE id <> NEW.id AND version_id IS NOT NULL
                -- Exact final-result collisions are handled by the existing UNIQUE
                -- index; this guard adds protection for shared intermediate versions.
                AND version_id <> NEW.version_id
            UNION ALL
            SELECT p.parent_version_id, c.stop_id, c.depth + 1
            FROM claimed c JOIN composition_versions p ON p.id = c.id
            WHERE c.depth < 256 AND p.parent_version_id IS NOT NULL AND p.parent_version_id IS NOT c.stop_id
        )
        SELECT 1 FROM produced JOIN claimed USING (id)
    ))
BEGIN
    SELECT RAISE(ABORT, 'immutable source-only request or invalid state revision');
END;

CREATE TRIGGER trg_source_only_execution_delete
BEFORE DELETE ON source_only_executions
BEGIN
    SELECT RAISE(ABORT, 'source-only acceptance records cannot be deleted');
END;