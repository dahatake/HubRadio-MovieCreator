CREATE TABLE jobs (
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
    state TEXT NOT NULL CHECK (
        state IN ('queued', 'running', 'succeeded', 'failed', 'cancelled', 'interrupted')
    ),
    created_at TEXT NOT NULL CHECK (
        length(created_at) > 0
        AND trim(created_at) = created_at
    ),
    updated_at TEXT NOT NULL CHECK (
        length(updated_at) > 0
        AND trim(updated_at) = updated_at
        AND updated_at >= created_at
    ),
    current_attempt_number INTEGER NOT NULL CHECK (
        current_attempt_number >= 0
        AND (state = 'queued' OR current_attempt_number >= 1)
    ),
    revision INTEGER NOT NULL CHECK (revision >= 0),
    FOREIGN KEY (project_id) REFERENCES projects (id)
        ON UPDATE RESTRICT ON DELETE RESTRICT
) STRICT;

CREATE INDEX idx_jobs_state_created_at
    ON jobs (state, created_at);
CREATE INDEX idx_jobs_project_id
    ON jobs (project_id);

CREATE TABLE job_attempts (
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
    job_id TEXT NOT NULL CHECK (
        length(job_id) = 36
        AND job_id = lower(job_id)
        AND substr(job_id, 9, 1) = '-'
        AND substr(job_id, 14, 1) = '-'
        AND substr(job_id, 19, 1) = '-'
        AND substr(job_id, 24, 1) = '-'
        AND length(replace(job_id, '-', '')) = 32
        AND replace(job_id, '-', '') NOT GLOB '*[^0-9a-f]*'
        AND job_id <> '00000000-0000-0000-0000-000000000000'
    ),
    sequence_number INTEGER NOT NULL CHECK (sequence_number > 0),
    state TEXT NOT NULL CHECK (
        state IN ('queued', 'running', 'succeeded', 'failed', 'cancelled', 'interrupted')
    ),
    queued_at TEXT NOT NULL CHECK (
        length(queued_at) > 0
        AND trim(queued_at) = queued_at
    ),
    started_at TEXT CHECK (
        started_at IS NULL
        OR (
            length(started_at) > 0
            AND trim(started_at) = started_at
            AND started_at >= queued_at
        )
    ),
    completed_at TEXT CHECK (
        completed_at IS NULL
        OR (
            length(completed_at) > 0
            AND trim(completed_at) = completed_at
            AND completed_at >= queued_at
            AND (started_at IS NULL OR completed_at >= started_at)
        )
    ),
    error_code TEXT CHECK (
        error_code IS NULL
        OR (
            length(error_code) BETWEEN 1 AND 128
            AND substr(error_code, 1, 1) GLOB '[a-z]'
            AND instr(error_code, '.') > 0
            AND error_code NOT GLOB '*[^a-z0-9_.]*'
            AND error_code NOT GLOB '*.[^a-z]*'
            AND instr(error_code, '__') = 0
            AND instr(error_code, '_.') = 0
            AND substr(error_code, -1, 1) GLOB '[a-z0-9]'
        )
    ),
    UNIQUE (job_id, id, sequence_number),
    FOREIGN KEY (job_id) REFERENCES jobs (id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    CHECK (
        (state = 'queued'
            AND started_at IS NULL
            AND completed_at IS NULL
            AND error_code IS NULL)
        OR (state = 'running'
            AND started_at IS NOT NULL
            AND completed_at IS NULL
            AND error_code IS NULL)
        OR (state = 'succeeded'
            AND started_at IS NOT NULL
            AND completed_at IS NOT NULL
            AND error_code IS NULL)
        OR (state = 'failed'
            AND started_at IS NOT NULL
            AND completed_at IS NOT NULL
            AND error_code IS NOT NULL)
        OR (state = 'cancelled'
            AND completed_at IS NOT NULL
            AND error_code IS NULL)
        OR (state = 'interrupted'
            AND started_at IS NOT NULL
            AND completed_at IS NOT NULL
            AND error_code IS NOT NULL)
    )
) STRICT;

CREATE UNIQUE INDEX idx_job_attempts_job_id_sequence_number
    ON job_attempts (job_id, sequence_number);

CREATE TABLE job_progress (
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
    job_id TEXT NOT NULL CHECK (
        length(job_id) = 36
        AND job_id = lower(job_id)
        AND substr(job_id, 9, 1) = '-'
        AND substr(job_id, 14, 1) = '-'
        AND substr(job_id, 19, 1) = '-'
        AND substr(job_id, 24, 1) = '-'
        AND length(replace(job_id, '-', '')) = 32
        AND replace(job_id, '-', '') NOT GLOB '*[^0-9a-f]*'
        AND job_id <> '00000000-0000-0000-0000-000000000000'
    ),
    job_attempt_id TEXT NOT NULL CHECK (
        length(job_attempt_id) = 36
        AND job_attempt_id = lower(job_attempt_id)
        AND substr(job_attempt_id, 9, 1) = '-'
        AND substr(job_attempt_id, 14, 1) = '-'
        AND substr(job_attempt_id, 19, 1) = '-'
        AND substr(job_attempt_id, 24, 1) = '-'
        AND length(replace(job_attempt_id, '-', '')) = 32
        AND replace(job_attempt_id, '-', '') NOT GLOB '*[^0-9a-f]*'
        AND job_attempt_id <> '00000000-0000-0000-0000-000000000000'
    ),
    sequence_number INTEGER NOT NULL CHECK (sequence_number > 0),
    recorded_at TEXT NOT NULL CHECK (
        length(recorded_at) > 0
        AND trim(recorded_at) = recorded_at
    ),
    completed_units INTEGER NOT NULL CHECK (completed_units >= 0),
    total_units INTEGER CHECK (
        total_units IS NULL
        OR (total_units > 0 AND completed_units <= total_units)
    ),
    phase TEXT CHECK (
        phase IS NULL
        OR (length(phase) > 0 AND trim(phase) = phase)
    ),
    message TEXT CHECK (
        message IS NULL
        OR (length(message) > 0 AND trim(message) = message)
    ),
    FOREIGN KEY (job_id, job_attempt_id, sequence_number)
        REFERENCES job_attempts (job_id, id, sequence_number)
        ON UPDATE RESTRICT ON DELETE RESTRICT
) STRICT;

CREATE INDEX idx_job_progress_attempt_recorded_at
    ON job_progress (job_attempt_id, recorded_at);

CREATE TABLE job_heartbeats (
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
    job_id TEXT NOT NULL CHECK (
        length(job_id) = 36
        AND job_id = lower(job_id)
        AND substr(job_id, 9, 1) = '-'
        AND substr(job_id, 14, 1) = '-'
        AND substr(job_id, 19, 1) = '-'
        AND substr(job_id, 24, 1) = '-'
        AND length(replace(job_id, '-', '')) = 32
        AND replace(job_id, '-', '') NOT GLOB '*[^0-9a-f]*'
        AND job_id <> '00000000-0000-0000-0000-000000000000'
    ),
    job_attempt_id TEXT NOT NULL CHECK (
        length(job_attempt_id) = 36
        AND job_attempt_id = lower(job_attempt_id)
        AND substr(job_attempt_id, 9, 1) = '-'
        AND substr(job_attempt_id, 14, 1) = '-'
        AND substr(job_attempt_id, 19, 1) = '-'
        AND substr(job_attempt_id, 24, 1) = '-'
        AND length(replace(job_attempt_id, '-', '')) = 32
        AND replace(job_attempt_id, '-', '') NOT GLOB '*[^0-9a-f]*'
        AND job_attempt_id <> '00000000-0000-0000-0000-000000000000'
    ),
    sequence_number INTEGER NOT NULL CHECK (sequence_number > 0),
    recorded_at TEXT NOT NULL CHECK (
        length(recorded_at) > 0
        AND trim(recorded_at) = recorded_at
    ),
    FOREIGN KEY (job_id, job_attempt_id, sequence_number)
        REFERENCES job_attempts (job_id, id, sequence_number)
        ON UPDATE RESTRICT ON DELETE RESTRICT
) STRICT;

CREATE INDEX idx_job_heartbeats_attempt_recorded_at
    ON job_heartbeats (job_attempt_id, recorded_at);