CREATE UNIQUE INDEX idx_jobs_id_project_id
    ON jobs (id, project_id);

CREATE TABLE ingest_workflows (
    job_id TEXT NOT NULL PRIMARY KEY CHECK (
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
    request_digest TEXT NOT NULL CHECK (
        length(request_digest) = 64
        AND request_digest = lower(request_digest)
        AND request_digest NOT GLOB '*[^0-9a-f]*'
    ),
    request_schema_version TEXT NOT NULL CHECK (
        length(request_schema_version) > 0
        AND trim(request_schema_version) = request_schema_version
    ),
    request_payload BLOB NOT NULL CHECK (
        length(request_payload) BETWEEN 1 AND 67108864
    ),
    checkpoint_stage INTEGER NOT NULL CHECK (
        checkpoint_stage BETWEEN 0 AND 9
    ),
    checkpoint_attempt_number INTEGER NOT NULL CHECK (
        checkpoint_attempt_number >= 0
    ),
    checkpoint_digest TEXT NOT NULL CHECK (
        length(checkpoint_digest) = 64
        AND checkpoint_digest = lower(checkpoint_digest)
        AND checkpoint_digest NOT GLOB '*[^0-9a-f]*'
    ),
    checkpoint_schema_version TEXT NOT NULL CHECK (
        length(checkpoint_schema_version) > 0
        AND trim(checkpoint_schema_version) = checkpoint_schema_version
    ),
    checkpoint_payload BLOB NOT NULL CHECK (
        length(checkpoint_payload) BETWEEN 1 AND 67108864
    ),
    revision INTEGER NOT NULL CHECK (revision >= 0),
    updated_at TEXT NOT NULL CHECK (
        length(updated_at) > 0
        AND trim(updated_at) = updated_at
    ),
    UNIQUE (job_id, project_id),
    CHECK (
        (checkpoint_stage = 0 AND checkpoint_attempt_number = 0)
        OR (checkpoint_stage > 0 AND checkpoint_attempt_number > 0)
    ),
    FOREIGN KEY (job_id, project_id) REFERENCES jobs (id, project_id)
        ON UPDATE RESTRICT ON DELETE RESTRICT
) STRICT;

CREATE INDEX idx_ingest_workflows_project_id
    ON ingest_workflows (project_id);
