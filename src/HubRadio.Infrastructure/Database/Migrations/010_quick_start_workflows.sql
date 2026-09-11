CREATE UNIQUE INDEX idx_automated_evaluations_quick_start_binding
    ON automated_evaluations (
        evaluation_id,
        project_id,
        composition_version_id,
        composition_version_digest,
        settings_snapshot_digest,
        aggregate_status,
        canonical_digest,
        artifact_id,
        artifact_digest
    );

CREATE TABLE quick_start_workflows (
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
    current_state TEXT NOT NULL CHECK (
        current_state IN (
            'editing',
            'automatedEvaluation',
            'ready',
            'readyWithWarnings',
            'failed',
            'exported'
        )
    ),
    revision INTEGER NOT NULL CHECK (revision > 0),
    project_revision INTEGER CHECK (
        project_revision IS NULL OR project_revision >= 0
    ),
    project_canonical_digest TEXT CHECK (
        project_canonical_digest IS NULL
        OR (
            length(project_canonical_digest) = 64
            AND project_canonical_digest = lower(project_canonical_digest)
            AND project_canonical_digest NOT GLOB '*[^0-9a-f]*'
        )
    ),
    settings_snapshot_digest TEXT CHECK (
        settings_snapshot_digest IS NULL
        OR (
            length(settings_snapshot_digest) = 64
            AND settings_snapshot_digest = lower(settings_snapshot_digest)
            AND settings_snapshot_digest NOT GLOB '*[^0-9a-f]*'
        )
    ),
    evaluation_id TEXT CHECK (
        evaluation_id IS NULL
        OR (
            length(evaluation_id) = 36
            AND evaluation_id = lower(evaluation_id)
            AND substr(evaluation_id, 9, 1) = '-'
            AND substr(evaluation_id, 14, 1) = '-'
            AND substr(evaluation_id, 19, 1) = '-'
            AND substr(evaluation_id, 24, 1) = '-'
            AND length(replace(evaluation_id, '-', '')) = 32
            AND replace(evaluation_id, '-', '') NOT GLOB '*[^0-9a-f]*'
            AND evaluation_id <> '00000000-0000-0000-0000-000000000000'
        )
    ),
    evaluation_aggregate_status TEXT CHECK (
        evaluation_aggregate_status IS NULL
        OR evaluation_aggregate_status IN (
            'PASS',
            'WARNING',
            'FAIL',
            'NOT EVALUATED'
        )
    ),
    evaluation_canonical_digest TEXT CHECK (
        evaluation_canonical_digest IS NULL
        OR (
            length(evaluation_canonical_digest) = 64
            AND evaluation_canonical_digest = lower(evaluation_canonical_digest)
            AND evaluation_canonical_digest NOT GLOB '*[^0-9a-f]*'
        )
    ),
    artifact_id TEXT CHECK (
        artifact_id IS NULL
        OR (
            length(artifact_id) = 36
            AND artifact_id = lower(artifact_id)
            AND substr(artifact_id, 9, 1) = '-'
            AND substr(artifact_id, 14, 1) = '-'
            AND substr(artifact_id, 19, 1) = '-'
            AND substr(artifact_id, 24, 1) = '-'
            AND length(replace(artifact_id, '-', '')) = 32
            AND replace(artifact_id, '-', '') NOT GLOB '*[^0-9a-f]*'
            AND artifact_id <> '00000000-0000-0000-0000-000000000000'
        )
    ),
    artifact_digest TEXT CHECK (
        artifact_digest IS NULL
        OR (
            length(artifact_digest) = 64
            AND artifact_digest = lower(artifact_digest)
            AND artifact_digest NOT GLOB '*[^0-9a-f]*'
        )
    ),
    self_review_count INTEGER NOT NULL CHECK (self_review_count >= 0),
    change_request_count INTEGER NOT NULL CHECK (change_request_count >= 0),
    event_chain_digest TEXT NOT NULL CHECK (
        length(event_chain_digest) = 64
        AND event_chain_digest = lower(event_chain_digest)
        AND event_chain_digest NOT GLOB '*[^0-9a-f]*'
    ),
    last_event_id TEXT NOT NULL CHECK (
        length(last_event_id) = 36
        AND last_event_id = lower(last_event_id)
        AND substr(last_event_id, 9, 1) = '-'
        AND substr(last_event_id, 14, 1) = '-'
        AND substr(last_event_id, 19, 1) = '-'
        AND substr(last_event_id, 24, 1) = '-'
        AND length(replace(last_event_id, '-', '')) = 32
        AND replace(last_event_id, '-', '') NOT GLOB '*[^0-9a-f]*'
        AND last_event_id <> '00000000-0000-0000-0000-000000000000'
    ),
    last_event_digest TEXT NOT NULL CHECK (
        length(last_event_digest) = 64
        AND last_event_digest = lower(last_event_digest)
        AND last_event_digest NOT GLOB '*[^0-9a-f]*'
    ),
    canonical_digest TEXT NOT NULL CHECK (
        length(canonical_digest) = 64
        AND canonical_digest = lower(canonical_digest)
        AND canonical_digest NOT GLOB '*[^0-9a-f]*'
    ),
    canonical_payload BLOB NOT NULL CHECK (
        length(canonical_payload) BETWEEN 1 AND 16384
    ),
    PRIMARY KEY (
        project_id,
        composition_version_id,
        composition_version_digest
    ),
    CHECK ((artifact_id IS NULL) = (artifact_digest IS NULL)),
    CHECK (
        (project_revision IS NULL) = (project_canonical_digest IS NULL)
    ),
    CHECK (
        (current_state = 'editing'
            AND project_revision IS NULL
            AND project_canonical_digest IS NULL
            AND settings_snapshot_digest IS NULL
            AND evaluation_id IS NULL
            AND evaluation_aggregate_status IS NULL
            AND evaluation_canonical_digest IS NULL
            AND artifact_id IS NULL)
        OR (current_state = 'automatedEvaluation'
            AND project_revision IS NOT NULL
            AND project_canonical_digest IS NOT NULL
            AND settings_snapshot_digest IS NOT NULL
            AND evaluation_id IS NULL
            AND evaluation_aggregate_status IS NULL
            AND evaluation_canonical_digest IS NULL
            AND artifact_id IS NULL)
        OR (current_state = 'failed'
            AND project_revision IS NOT NULL
            AND project_canonical_digest IS NOT NULL
            AND settings_snapshot_digest IS NOT NULL
            AND evaluation_id IS NOT NULL
            AND evaluation_aggregate_status IS NOT NULL
            AND evaluation_canonical_digest IS NOT NULL)
        OR (current_state IN ('ready', 'readyWithWarnings', 'exported')
            AND project_revision IS NOT NULL
            AND project_canonical_digest IS NOT NULL
            AND settings_snapshot_digest IS NOT NULL
            AND evaluation_id IS NOT NULL
            AND evaluation_aggregate_status IS NOT NULL
            AND evaluation_canonical_digest IS NOT NULL
            AND artifact_id IS NOT NULL)
    ),
    FOREIGN KEY (
        composition_version_id,
        project_id,
        composition_version_digest
    ) REFERENCES composition_versions (id, project_id, content_digest)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    FOREIGN KEY (evaluation_id)
        REFERENCES automated_evaluations (evaluation_id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    FOREIGN KEY (artifact_id, project_id, artifact_digest)
        REFERENCES artifacts (id, project_id, content_digest)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    FOREIGN KEY (
        evaluation_id,
        project_id,
        composition_version_id,
        composition_version_digest,
        settings_snapshot_digest,
        evaluation_aggregate_status,
        evaluation_canonical_digest,
        artifact_id,
        artifact_digest
    ) REFERENCES automated_evaluations (
        evaluation_id,
        project_id,
        composition_version_id,
        composition_version_digest,
        settings_snapshot_digest,
        aggregate_status,
        canonical_digest,
        artifact_id,
        artifact_digest
    ) ON UPDATE RESTRICT ON DELETE RESTRICT
) STRICT;

CREATE INDEX idx_quick_start_workflows_version
    ON quick_start_workflows (
        project_id,
        composition_version_id,
        composition_version_digest
    );

CREATE TABLE quick_start_state_events (
    project_id TEXT NOT NULL,
    composition_version_id TEXT NOT NULL,
    composition_version_digest TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK (revision > 0),
    event_id TEXT NOT NULL UNIQUE CHECK (
        length(event_id) = 36
        AND event_id = lower(event_id)
        AND substr(event_id, 9, 1) = '-'
        AND substr(event_id, 14, 1) = '-'
        AND substr(event_id, 19, 1) = '-'
        AND substr(event_id, 24, 1) = '-'
        AND length(replace(event_id, '-', '')) = 32
        AND replace(event_id, '-', '') NOT GLOB '*[^0-9a-f]*'
        AND event_id <> '00000000-0000-0000-0000-000000000000'
    ),
    kind TEXT NOT NULL CHECK (
        kind IN (
            'StartedEditing',
            'EvaluationStarted',
            'TechnicalReevaluationStarted',
            'EvaluationCompleted',
            'SelfReviewed',
            'ChangesRequested',
            'Exported'
        )
    ),
    actor_kind TEXT NOT NULL CHECK (actor_kind IN ('user', 'system')),
    actor_id TEXT NOT NULL CHECK (
        length(actor_id) BETWEEN 1 AND 256
        AND trim(actor_id) = actor_id
        AND instr(actor_id, char(0)) = 0
    ),
    occurred_at TEXT NOT NULL CHECK (
        length(occurred_at) = 33
        AND substr(occurred_at, 5, 1) = '-'
        AND substr(occurred_at, 8, 1) = '-'
        AND substr(occurred_at, 11, 1) = 'T'
        AND substr(occurred_at, 14, 1) = ':'
        AND substr(occurred_at, 17, 1) = ':'
        AND substr(occurred_at, 20, 1) = '.'
        AND substr(occurred_at, 28, 6) = '+00:00'
    ),
    project_revision INTEGER CHECK (
        project_revision IS NULL OR project_revision >= 0
    ),
    project_canonical_digest TEXT CHECK (
        project_canonical_digest IS NULL
        OR (
            length(project_canonical_digest) = 64
            AND project_canonical_digest = lower(project_canonical_digest)
            AND project_canonical_digest NOT GLOB '*[^0-9a-f]*'
        )
    ),
    settings_snapshot_digest TEXT CHECK (
        settings_snapshot_digest IS NULL
        OR (
            length(settings_snapshot_digest) = 64
            AND settings_snapshot_digest = lower(settings_snapshot_digest)
            AND settings_snapshot_digest NOT GLOB '*[^0-9a-f]*'
        )
    ),
    evaluation_id TEXT,
    evaluation_evaluated_at TEXT CHECK (
        evaluation_evaluated_at IS NULL
        OR (
            length(evaluation_evaluated_at) = 33
            AND substr(evaluation_evaluated_at, 5, 1) = '-'
            AND substr(evaluation_evaluated_at, 8, 1) = '-'
            AND substr(evaluation_evaluated_at, 11, 1) = 'T'
            AND substr(evaluation_evaluated_at, 14, 1) = ':'
            AND substr(evaluation_evaluated_at, 17, 1) = ':'
            AND substr(evaluation_evaluated_at, 20, 1) = '.'
            AND substr(evaluation_evaluated_at, 28, 6) = '+00:00'
            AND evaluation_evaluated_at <= occurred_at
        )
    ),
    evaluation_aggregate_status TEXT CHECK (
        evaluation_aggregate_status IS NULL
        OR evaluation_aggregate_status IN (
            'PASS',
            'WARNING',
            'FAIL',
            'NOT EVALUATED'
        )
    ),
    evaluation_canonical_digest TEXT CHECK (
        evaluation_canonical_digest IS NULL
        OR (
            length(evaluation_canonical_digest) = 64
            AND evaluation_canonical_digest = lower(evaluation_canonical_digest)
            AND evaluation_canonical_digest NOT GLOB '*[^0-9a-f]*'
        )
    ),
    artifact_id TEXT,
    artifact_digest TEXT CHECK (
        artifact_digest IS NULL
        OR (
            length(artifact_digest) = 64
            AND artifact_digest = lower(artifact_digest)
            AND artifact_digest NOT GLOB '*[^0-9a-f]*'
        )
    ),
    evaluation_result_state TEXT CHECK (
        evaluation_result_state IS NULL
        OR evaluation_result_state IN ('ready', 'readyWithWarnings', 'failed')
    ),
    reason_code TEXT CHECK (
        reason_code IS NULL
        OR (
            length(reason_code) BETWEEN 3 AND 128
            AND reason_code = lower(reason_code)
            AND reason_code NOT GLOB '*[^a-z0-9_.]*'
            AND substr(reason_code, 1, 1) GLOB '[a-z]'
            AND instr(reason_code, '.') > 1
            AND instr(reason_code, '..') = 0
            AND instr(reason_code, '__') = 0
            AND instr(reason_code, '._') = 0
            AND instr(reason_code, '_.') = 0
            AND reason_code NOT GLOB '*.[^a-z]*'
            AND substr(reason_code, -1, 1) GLOB '[a-z0-9]'
        )
    ),
    publication_scope TEXT CHECK (
        publication_scope IS NULL OR publication_scope = 'InternalOnly'
    ),
    rights_allowed_for_internal_use INTEGER CHECK (
        rights_allowed_for_internal_use IS NULL
        OR rights_allowed_for_internal_use IN (0, 1)
    ),
    external_destination_requested INTEGER CHECK (
        external_destination_requested IS NULL
        OR external_destination_requested IN (0, 1)
    ),
    destination_kind TEXT CHECK (
        destination_kind IS NULL OR destination_kind = 'localInternalDelivery'
    ),
    authorized_state TEXT CHECK (
        authorized_state IS NULL OR authorized_state IN ('ready', 'readyWithWarnings')
    ),
    authorized_revision INTEGER CHECK (
        authorized_revision IS NULL OR authorized_revision > 0
    ),
    canonical_digest TEXT NOT NULL CHECK (
        length(canonical_digest) = 64
        AND canonical_digest = lower(canonical_digest)
        AND canonical_digest NOT GLOB '*[^0-9a-f]*'
    ),
    canonical_payload BLOB NOT NULL CHECK (
        length(canonical_payload) BETWEEN 1 AND 16384
    ),
    PRIMARY KEY (
        project_id,
        composition_version_id,
        composition_version_digest,
        revision
    ),
    CHECK ((artifact_id IS NULL) = (artifact_digest IS NULL)),
    CHECK (
        (project_revision IS NULL) = (project_canonical_digest IS NULL)
    ),
    CHECK (
        (kind = 'EvaluationCompleted' AND evaluation_evaluated_at IS NOT NULL)
        OR (kind <> 'EvaluationCompleted' AND evaluation_evaluated_at IS NULL)
    ),
    CHECK (
        (kind = 'StartedEditing'
            AND revision = 1
            AND actor_kind = 'user'
            AND project_revision IS NULL
            AND project_canonical_digest IS NULL
            AND settings_snapshot_digest IS NULL
            AND evaluation_id IS NULL
            AND evaluation_aggregate_status IS NULL
            AND evaluation_canonical_digest IS NULL
            AND artifact_id IS NULL
            AND evaluation_result_state IS NULL
            AND reason_code IS NULL
            AND publication_scope IS NULL)
        OR (kind = 'EvaluationStarted'
            AND revision > 1
            AND actor_kind = 'system'
            AND actor_id = 'automated-evaluation-v2'
            AND project_revision IS NOT NULL
            AND project_canonical_digest IS NOT NULL
            AND settings_snapshot_digest IS NOT NULL
            AND evaluation_id IS NULL
            AND evaluation_aggregate_status IS NULL
            AND evaluation_canonical_digest IS NULL
            AND artifact_id IS NULL
            AND evaluation_result_state IS NULL
            AND reason_code IS NULL
            AND publication_scope IS NULL)
        OR (kind = 'TechnicalReevaluationStarted'
            AND revision > 2
            AND actor_kind = 'system'
            AND actor_id = 'automated-evaluation-v2'
            AND project_revision IS NOT NULL
            AND project_canonical_digest IS NOT NULL
            AND settings_snapshot_digest IS NOT NULL
            AND evaluation_id IS NULL
            AND evaluation_aggregate_status IS NULL
            AND evaluation_canonical_digest IS NULL
            AND artifact_id IS NULL
            AND evaluation_result_state IS NULL
            AND reason_code IS NULL
            AND publication_scope IS NULL)
        OR (kind = 'EvaluationCompleted'
            AND revision > 2
            AND actor_kind = 'system'
            AND actor_id = 'automated-evaluation-v2'
            AND project_revision IS NOT NULL
            AND project_canonical_digest IS NOT NULL
            AND settings_snapshot_digest IS NOT NULL
            AND evaluation_id IS NOT NULL
            AND evaluation_aggregate_status IS NOT NULL
            AND evaluation_canonical_digest IS NOT NULL
            AND evaluation_result_state IS NOT NULL
            AND reason_code IS NULL
            AND publication_scope IS NULL
            AND (artifact_id IS NOT NULL OR evaluation_result_state = 'failed'))
        OR (kind = 'SelfReviewed'
            AND revision > 1
            AND actor_kind = 'user'
            AND project_revision IS NULL
            AND project_canonical_digest IS NULL
            AND settings_snapshot_digest IS NULL
            AND evaluation_id IS NULL
            AND evaluation_aggregate_status IS NULL
            AND evaluation_canonical_digest IS NULL
            AND artifact_id IS NULL
            AND evaluation_result_state IS NULL
            AND reason_code IS NULL
            AND publication_scope IS NULL)
        OR (kind = 'ChangesRequested'
            AND revision > 1
            AND actor_kind = 'user'
            AND project_revision IS NULL
            AND project_canonical_digest IS NULL
            AND settings_snapshot_digest IS NULL
            AND evaluation_id IS NULL
            AND evaluation_aggregate_status IS NULL
            AND evaluation_canonical_digest IS NULL
            AND artifact_id IS NULL
            AND evaluation_result_state IS NULL
            AND reason_code IS NOT NULL
            AND publication_scope IS NULL)
        OR (kind = 'Exported'
            AND revision > 3
            AND actor_kind = 'user'
            AND project_revision IS NOT NULL
            AND project_canonical_digest IS NOT NULL
            AND settings_snapshot_digest IS NOT NULL
            AND evaluation_id IS NOT NULL
            AND evaluation_aggregate_status IS NOT NULL
            AND evaluation_canonical_digest IS NOT NULL
            AND artifact_id IS NOT NULL
            AND evaluation_result_state IS NULL
            AND reason_code IS NULL
            AND publication_scope = 'InternalOnly'
            AND rights_allowed_for_internal_use = 1
            AND external_destination_requested = 0
            AND destination_kind = 'localInternalDelivery'
            AND authorized_state IN ('ready', 'readyWithWarnings')
            AND authorized_revision = revision - 1)
    ),
    CHECK (
        (kind = 'Exported'
            AND rights_allowed_for_internal_use IS NOT NULL
            AND external_destination_requested IS NOT NULL
            AND destination_kind IS NOT NULL
            AND authorized_state IS NOT NULL
            AND authorized_revision IS NOT NULL)
        OR (kind <> 'Exported'
            AND rights_allowed_for_internal_use IS NULL
            AND external_destination_requested IS NULL
            AND destination_kind IS NULL
            AND authorized_state IS NULL
            AND authorized_revision IS NULL)
    ),
    FOREIGN KEY (
        project_id,
        composition_version_id,
        composition_version_digest
    ) REFERENCES quick_start_workflows (
        project_id,
        composition_version_id,
        composition_version_digest
    ) ON UPDATE RESTRICT ON DELETE RESTRICT,
    FOREIGN KEY (
        composition_version_id,
        project_id,
        composition_version_digest
    ) REFERENCES composition_versions (
        id,
        project_id,
        content_digest
    ) ON UPDATE RESTRICT ON DELETE RESTRICT,
    FOREIGN KEY (evaluation_id)
        REFERENCES automated_evaluations (evaluation_id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    FOREIGN KEY (artifact_id, project_id, artifact_digest)
        REFERENCES artifacts (id, project_id, content_digest)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    FOREIGN KEY (
        evaluation_id,
        project_id,
        composition_version_id,
        composition_version_digest,
        settings_snapshot_digest,
        evaluation_aggregate_status,
        evaluation_canonical_digest,
        artifact_id,
        artifact_digest
    ) REFERENCES automated_evaluations (
        evaluation_id,
        project_id,
        composition_version_id,
        composition_version_digest,
        settings_snapshot_digest,
        aggregate_status,
        canonical_digest,
        artifact_id,
        artifact_digest
    ) ON UPDATE RESTRICT ON DELETE RESTRICT
) STRICT;

CREATE INDEX idx_quick_start_state_events_version
    ON quick_start_state_events (
        project_id,
        composition_version_id,
        composition_version_digest,
        revision
    );

CREATE TRIGGER trg_quick_start_workflow_insert_shape
BEFORE INSERT ON quick_start_workflows
WHEN NEW.current_state <> 'editing'
    OR NEW.revision <> 1
    OR NEW.self_review_count <> 0
    OR NEW.change_request_count <> 0
BEGIN
    SELECT RAISE(ABORT, 'a quick-start workflow must begin at editing revision one');
END;

CREATE TRIGGER trg_quick_start_workflow_update_transition
BEFORE UPDATE ON quick_start_workflows
WHEN NEW.project_id <> OLD.project_id
    OR NEW.composition_version_id <> OLD.composition_version_id
    OR NEW.composition_version_digest <> OLD.composition_version_digest
    OR NEW.revision <> OLD.revision + 1
    OR NEW.self_review_count < OLD.self_review_count
    OR NEW.change_request_count < OLD.change_request_count
    OR NOT (
        (OLD.current_state = 'editing'
            AND NEW.current_state IN ('editing', 'automatedEvaluation'))
        OR (OLD.current_state = 'automatedEvaluation'
            AND NEW.current_state IN ('ready', 'readyWithWarnings', 'failed'))
        OR (OLD.current_state IN ('ready', 'readyWithWarnings')
            AND NEW.current_state IN (OLD.current_state, 'exported'))
        OR (OLD.current_state = 'failed' AND NEW.current_state = 'failed')
        OR (OLD.current_state IN ('automatedEvaluation', 'ready', 'readyWithWarnings', 'failed')
            AND NEW.current_state = 'automatedEvaluation'
            AND NEW.settings_snapshot_digest IS OLD.settings_snapshot_digest)
    )
BEGIN
    SELECT RAISE(ABORT, 'invalid quick-start workflow snapshot transition');
END;

CREATE TRIGGER trg_quick_start_workflow_update_event_match
BEFORE UPDATE ON quick_start_workflows
WHEN NOT EXISTS (
    SELECT 1
    FROM quick_start_state_events AS event
    WHERE event.project_id = NEW.project_id
      AND event.composition_version_id = NEW.composition_version_id
      AND event.composition_version_digest = NEW.composition_version_digest
      AND event.revision = NEW.revision
      AND event.event_id = NEW.last_event_id
      AND event.canonical_digest = NEW.last_event_digest
      AND (
        (event.kind = 'EvaluationStarted'
            AND OLD.current_state = 'editing'
            AND NEW.current_state = 'automatedEvaluation'
            AND event.project_revision IS NEW.project_revision
            AND event.project_canonical_digest IS NEW.project_canonical_digest
            AND OLD.project_revision IS NULL
            AND OLD.project_canonical_digest IS NULL
            AND event.settings_snapshot_digest IS NEW.settings_snapshot_digest
            AND OLD.settings_snapshot_digest IS NULL
            AND OLD.evaluation_id IS NULL
            AND OLD.evaluation_aggregate_status IS NULL
            AND OLD.evaluation_canonical_digest IS NULL
            AND OLD.artifact_id IS NULL
            AND OLD.artifact_digest IS NULL
            AND NEW.evaluation_id IS NULL
            AND NEW.evaluation_aggregate_status IS NULL
            AND NEW.evaluation_canonical_digest IS NULL
            AND NEW.artifact_id IS NULL
            AND NEW.artifact_digest IS NULL
            AND NEW.self_review_count = OLD.self_review_count
            AND NEW.change_request_count = OLD.change_request_count)
        OR (event.kind = 'TechnicalReevaluationStarted'
            AND OLD.current_state IN ('automatedEvaluation', 'ready', 'readyWithWarnings', 'failed')
            AND NEW.current_state = 'automatedEvaluation'
            AND event.project_revision IS NEW.project_revision
            AND event.project_canonical_digest IS NEW.project_canonical_digest
            AND event.settings_snapshot_digest IS OLD.settings_snapshot_digest
            AND NEW.settings_snapshot_digest IS OLD.settings_snapshot_digest
            AND NEW.evaluation_id IS NULL
            AND NEW.evaluation_aggregate_status IS NULL
            AND NEW.evaluation_canonical_digest IS NULL
            AND NEW.artifact_id IS NULL
            AND NEW.artifact_digest IS NULL
            AND NEW.self_review_count = OLD.self_review_count
            AND NEW.change_request_count = OLD.change_request_count)
        OR (event.kind = 'EvaluationCompleted'
            AND OLD.current_state = 'automatedEvaluation'
            AND NEW.current_state IS event.evaluation_result_state
            AND event.project_revision IS OLD.project_revision
            AND event.project_revision IS NEW.project_revision
            AND event.project_canonical_digest IS OLD.project_canonical_digest
            AND event.project_canonical_digest IS NEW.project_canonical_digest
            AND event.settings_snapshot_digest IS OLD.settings_snapshot_digest
            AND NEW.settings_snapshot_digest IS OLD.settings_snapshot_digest
            AND event.evaluation_id IS NEW.evaluation_id
            AND event.evaluation_aggregate_status IS NEW.evaluation_aggregate_status
            AND event.evaluation_canonical_digest IS NEW.evaluation_canonical_digest
            AND event.artifact_id IS NEW.artifact_id
            AND event.artifact_digest IS NEW.artifact_digest
            AND NEW.self_review_count = OLD.self_review_count
            AND NEW.change_request_count = OLD.change_request_count)
        OR (event.kind = 'SelfReviewed'
            AND OLD.current_state IN ('ready', 'readyWithWarnings', 'failed')
            AND NEW.current_state = OLD.current_state
            AND NEW.project_revision IS OLD.project_revision
            AND NEW.project_canonical_digest IS OLD.project_canonical_digest
            AND NEW.settings_snapshot_digest IS OLD.settings_snapshot_digest
            AND NEW.evaluation_id IS OLD.evaluation_id
            AND NEW.evaluation_aggregate_status IS OLD.evaluation_aggregate_status
            AND NEW.evaluation_canonical_digest IS OLD.evaluation_canonical_digest
            AND NEW.artifact_id IS OLD.artifact_id
            AND NEW.artifact_digest IS OLD.artifact_digest
            AND NEW.self_review_count = OLD.self_review_count + 1
            AND NEW.change_request_count = OLD.change_request_count)
        OR (event.kind = 'ChangesRequested'
            AND OLD.current_state IN ('editing', 'ready', 'readyWithWarnings', 'failed')
            AND NEW.current_state = OLD.current_state
            AND NEW.project_revision IS OLD.project_revision
            AND NEW.project_canonical_digest IS OLD.project_canonical_digest
            AND NEW.settings_snapshot_digest IS OLD.settings_snapshot_digest
            AND NEW.evaluation_id IS OLD.evaluation_id
            AND NEW.evaluation_aggregate_status IS OLD.evaluation_aggregate_status
            AND NEW.evaluation_canonical_digest IS OLD.evaluation_canonical_digest
            AND NEW.artifact_id IS OLD.artifact_id
            AND NEW.artifact_digest IS OLD.artifact_digest
            AND NEW.self_review_count = OLD.self_review_count
            AND NEW.change_request_count = OLD.change_request_count + 1)
        OR (event.kind = 'Exported'
            AND OLD.current_state IN ('ready', 'readyWithWarnings')
            AND NEW.current_state = 'exported'
            AND event.project_revision IS NEW.project_revision
            AND event.project_canonical_digest IS NEW.project_canonical_digest
            AND event.settings_snapshot_digest IS NEW.settings_snapshot_digest
            AND event.evaluation_id IS NEW.evaluation_id
            AND event.evaluation_aggregate_status IS NEW.evaluation_aggregate_status
            AND event.evaluation_canonical_digest IS NEW.evaluation_canonical_digest
            AND event.artifact_id IS NEW.artifact_id
            AND event.artifact_digest IS NEW.artifact_digest
            AND event.authorized_state = OLD.current_state
            AND event.authorized_revision = OLD.revision
            AND NEW.project_revision IS OLD.project_revision
            AND NEW.project_canonical_digest IS OLD.project_canonical_digest
            AND NEW.settings_snapshot_digest IS OLD.settings_snapshot_digest
            AND NEW.evaluation_id IS OLD.evaluation_id
            AND NEW.evaluation_aggregate_status IS OLD.evaluation_aggregate_status
            AND NEW.evaluation_canonical_digest IS OLD.evaluation_canonical_digest
            AND NEW.artifact_id IS OLD.artifact_id
            AND NEW.artifact_digest IS OLD.artifact_digest
            AND NEW.self_review_count = OLD.self_review_count
            AND NEW.change_request_count = OLD.change_request_count)
      )
)
BEGIN
    SELECT RAISE(ABORT, 'quick-start workflow update requires its exact state event');
END;

CREATE TRIGGER trg_quick_start_workflow_evaluation_insert
BEFORE INSERT ON quick_start_workflows
WHEN NEW.evaluation_id IS NOT NULL
AND NOT EXISTS (
    SELECT 1
    FROM automated_evaluations AS evaluation
    WHERE evaluation.evaluation_id = NEW.evaluation_id
      AND evaluation.project_id = NEW.project_id
      AND evaluation.composition_version_id = NEW.composition_version_id
      AND evaluation.composition_version_digest = NEW.composition_version_digest
      AND evaluation.settings_snapshot_digest = NEW.settings_snapshot_digest
    AND evaluation.aggregate_status = NEW.evaluation_aggregate_status
      AND evaluation.canonical_digest = NEW.evaluation_canonical_digest
      AND evaluation.artifact_id IS NEW.artifact_id
      AND evaluation.artifact_digest IS NEW.artifact_digest
)
BEGIN
    SELECT RAISE(ABORT, 'quick-start workflow evaluation binding mismatch');
END;

CREATE TRIGGER trg_quick_start_workflow_evaluation_update
BEFORE UPDATE ON quick_start_workflows
WHEN NEW.evaluation_id IS NOT NULL
AND NOT EXISTS (
    SELECT 1
    FROM automated_evaluations AS evaluation
    WHERE evaluation.evaluation_id = NEW.evaluation_id
      AND evaluation.project_id = NEW.project_id
      AND evaluation.composition_version_id = NEW.composition_version_id
      AND evaluation.composition_version_digest = NEW.composition_version_digest
      AND evaluation.settings_snapshot_digest = NEW.settings_snapshot_digest
    AND evaluation.aggregate_status = NEW.evaluation_aggregate_status
      AND evaluation.canonical_digest = NEW.evaluation_canonical_digest
      AND evaluation.artifact_id IS NEW.artifact_id
      AND evaluation.artifact_digest IS NEW.artifact_digest
)
BEGIN
    SELECT RAISE(ABORT, 'quick-start workflow evaluation binding mismatch');
END;

CREATE TRIGGER trg_quick_start_workflow_completed_binding_immutable
BEFORE UPDATE ON quick_start_workflows
WHEN OLD.evaluation_id IS NOT NULL
AND (
    NEW.project_revision IS NOT OLD.project_revision
    OR NEW.project_canonical_digest IS NOT OLD.project_canonical_digest
    OR NEW.settings_snapshot_digest IS NOT OLD.settings_snapshot_digest
    OR NEW.evaluation_id IS NOT OLD.evaluation_id
    OR NEW.evaluation_aggregate_status IS NOT OLD.evaluation_aggregate_status
    OR NEW.evaluation_canonical_digest IS NOT OLD.evaluation_canonical_digest
    OR NEW.artifact_id IS NOT OLD.artifact_id
    OR NEW.artifact_digest IS NOT OLD.artifact_digest
)
AND NOT (
    NEW.current_state = 'automatedEvaluation'
    AND NEW.project_revision IS NOT NULL
    AND NEW.project_canonical_digest IS NOT NULL
    AND NEW.settings_snapshot_digest IS OLD.settings_snapshot_digest
    AND NEW.evaluation_id IS NULL
    AND NEW.evaluation_aggregate_status IS NULL
    AND NEW.evaluation_canonical_digest IS NULL
    AND NEW.artifact_id IS NULL
    AND NEW.artifact_digest IS NULL
    AND EXISTS (
        SELECT 1
        FROM quick_start_state_events AS event
        WHERE event.project_id = NEW.project_id
          AND event.composition_version_id = NEW.composition_version_id
          AND event.composition_version_digest = NEW.composition_version_digest
          AND event.revision = NEW.revision
          AND event.event_id = NEW.last_event_id
          AND event.canonical_digest = NEW.last_event_digest
          AND event.kind = 'TechnicalReevaluationStarted'
          AND event.project_revision IS NEW.project_revision
          AND event.project_canonical_digest IS NEW.project_canonical_digest
          AND event.settings_snapshot_digest IS NEW.settings_snapshot_digest
    )
)
BEGIN
    SELECT RAISE(ABORT, 'a completed quick-start evaluation binding is immutable');
END;

CREATE TRIGGER trg_quick_start_evaluation_immutable_update
BEFORE UPDATE ON automated_evaluations
WHEN EXISTS (
    SELECT 1
    FROM quick_start_workflows AS workflow
    WHERE workflow.evaluation_id = OLD.evaluation_id
)
OR EXISTS (
    SELECT 1
    FROM quick_start_state_events AS event
    WHERE event.evaluation_id = OLD.evaluation_id
)
BEGIN
    SELECT RAISE(ABORT, 'an evaluation referenced by quick-start history is immutable');
END;

CREATE TRIGGER trg_quick_start_evaluation_rule_immutable_insert
BEFORE INSERT ON automated_evaluation_rules
WHEN EXISTS (
    SELECT 1
    FROM quick_start_workflows AS workflow
    WHERE workflow.evaluation_id = NEW.evaluation_id
)
OR EXISTS (
    SELECT 1
    FROM quick_start_state_events AS event
    WHERE event.evaluation_id = NEW.evaluation_id
)
BEGIN
    SELECT RAISE(ABORT, 'rules for an evaluation referenced by quick-start history are immutable');
END;

CREATE TRIGGER trg_quick_start_evaluation_rule_immutable_update
BEFORE UPDATE ON automated_evaluation_rules
WHEN EXISTS (
    SELECT 1
    FROM quick_start_workflows AS workflow
    WHERE workflow.evaluation_id IN (OLD.evaluation_id, NEW.evaluation_id)
)
OR EXISTS (
    SELECT 1
    FROM quick_start_state_events AS event
    WHERE event.evaluation_id IN (OLD.evaluation_id, NEW.evaluation_id)
)
BEGIN
    SELECT RAISE(ABORT, 'rules for an evaluation referenced by quick-start history are immutable');
END;

CREATE TRIGGER trg_quick_start_evaluation_rule_immutable_delete
BEFORE DELETE ON automated_evaluation_rules
WHEN EXISTS (
    SELECT 1
    FROM quick_start_workflows AS workflow
    WHERE workflow.evaluation_id = OLD.evaluation_id
)
OR EXISTS (
    SELECT 1
    FROM quick_start_state_events AS event
    WHERE event.evaluation_id = OLD.evaluation_id
)
BEGIN
    SELECT RAISE(ABORT, 'rules for an evaluation referenced by quick-start history are immutable');
END;

CREATE TRIGGER trg_quick_start_render_artifact_immutable_update
BEFORE UPDATE OF
    id,
    project_id,
    kind,
    storage_reference,
    content_digest,
    byte_size,
    created_at
ON artifacts
WHEN OLD.kind = 'Render'
AND (
    EXISTS (
        SELECT 1
        FROM quick_start_workflows AS workflow
        WHERE workflow.artifact_id = OLD.id
    )
    OR EXISTS (
        SELECT 1
        FROM quick_start_state_events AS event
        WHERE event.artifact_id = OLD.id
    )
)
BEGIN
    SELECT RAISE(ABORT, 'a render artifact referenced by quick-start history is immutable');
END;

CREATE TRIGGER trg_quick_start_render_artifact_immutable_delete
BEFORE DELETE ON artifacts
WHEN OLD.kind = 'Render'
AND (
    EXISTS (
        SELECT 1
        FROM quick_start_workflows AS workflow
        WHERE workflow.artifact_id = OLD.id
    )
    OR EXISTS (
        SELECT 1
        FROM quick_start_state_events AS event
        WHERE event.artifact_id = OLD.id
    )
)
BEGIN
    SELECT RAISE(ABORT, 'a render artifact referenced by quick-start history is immutable');
END;

CREATE TRIGGER trg_quick_start_event_evaluation_insert
BEFORE INSERT ON quick_start_state_events
WHEN NEW.evaluation_id IS NOT NULL
AND (
    NOT EXISTS (
        SELECT 1
        FROM automated_evaluations AS evaluation
        WHERE evaluation.evaluation_id = NEW.evaluation_id
          AND evaluation.project_id = NEW.project_id
          AND evaluation.composition_version_id = NEW.composition_version_id
          AND evaluation.composition_version_digest = NEW.composition_version_digest
          AND evaluation.settings_snapshot_digest = NEW.settings_snapshot_digest
          AND evaluation.aggregate_status = NEW.evaluation_aggregate_status
          AND evaluation.canonical_digest = NEW.evaluation_canonical_digest
          AND evaluation.artifact_id IS NEW.artifact_id
          AND evaluation.artifact_digest IS NEW.artifact_digest
          AND (
              NEW.kind <> 'EvaluationCompleted'
              OR evaluation.evaluated_at = NEW.evaluation_evaluated_at
          )
    )
    OR (
        NEW.kind = 'EvaluationCompleted'
        AND NOT EXISTS (
            SELECT 1
            FROM quick_start_state_events AS preceding
            WHERE preceding.project_id = NEW.project_id
              AND preceding.composition_version_id = NEW.composition_version_id
              AND preceding.composition_version_digest = NEW.composition_version_digest
              AND preceding.revision = NEW.revision - 1
              AND preceding.kind IN (
                  'EvaluationStarted',
                  'TechnicalReevaluationStarted'
              )
              AND preceding.project_revision IS NEW.project_revision
              AND preceding.project_canonical_digest IS NEW.project_canonical_digest
              AND preceding.settings_snapshot_digest = NEW.settings_snapshot_digest
              AND preceding.occurred_at <= NEW.evaluation_evaluated_at
        )
    )
    OR (
        NEW.kind = 'EvaluationCompleted'
        AND EXISTS (
            SELECT 1
            FROM quick_start_state_events AS completed
            WHERE completed.project_id = NEW.project_id
              AND completed.composition_version_id = NEW.composition_version_id
              AND completed.composition_version_digest = NEW.composition_version_digest
              AND completed.kind = 'EvaluationCompleted'
              AND completed.evaluation_id = NEW.evaluation_id
        )
    )
)
BEGIN
    SELECT RAISE(ABORT, 'quick-start event evaluation binding mismatch');
END;

CREATE TRIGGER trg_quick_start_event_immutable_update
BEFORE UPDATE ON quick_start_state_events
BEGIN
    SELECT RAISE(ABORT, 'quick-start workflow events are append-only');
END;

CREATE TRIGGER trg_quick_start_event_immutable_delete
BEFORE DELETE ON quick_start_state_events
BEGIN
    SELECT RAISE(ABORT, 'quick-start workflow events are append-only');
END;