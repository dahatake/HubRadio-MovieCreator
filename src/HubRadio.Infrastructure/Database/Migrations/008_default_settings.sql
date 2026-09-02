CREATE TABLE default_settings_profiles (
    profile_id TEXT NOT NULL CHECK (
        length(profile_id) = 36
        AND profile_id = lower(profile_id)
        AND substr(profile_id, 9, 1) = '-'
        AND substr(profile_id, 14, 1) = '-'
        AND substr(profile_id, 19, 1) = '-'
        AND substr(profile_id, 24, 1) = '-'
        AND length(replace(profile_id, '-', '')) = 32
        AND replace(profile_id, '-', '') NOT GLOB '*[^0-9a-f]*'
        AND profile_id <> '00000000-0000-0000-0000-000000000000'
    ),
    profile_version TEXT NOT NULL CHECK (
        length(profile_version) BETWEEN 5 AND 32
        AND profile_version NOT GLOB '*[^0-9.]*'
        AND length(profile_version) - length(replace(profile_version, '.', '')) = 2
        AND instr(profile_version, '.') BETWEEN 2 AND 11
        AND instr(substr(profile_version, instr(profile_version, '.') + 1), '.') BETWEEN 2 AND 11
        AND length(profile_version)
            - instr(profile_version, '.')
            - instr(substr(profile_version, instr(profile_version, '.') + 1), '.')
            BETWEEN 1 AND 10
        AND (
            instr(profile_version, '.') = 2
            OR substr(profile_version, 1, 1) <> '0'
        )
        AND (
            instr(substr(profile_version, instr(profile_version, '.') + 1), '.') = 2
            OR substr(profile_version, instr(profile_version, '.') + 1, 1) <> '0'
        )
        AND (
            length(profile_version)
                - instr(profile_version, '.')
                - instr(substr(profile_version, instr(profile_version, '.') + 1), '.') = 1
            OR substr(
                profile_version,
                instr(profile_version, '.')
                    + instr(substr(profile_version, instr(profile_version, '.') + 1), '.')
                    + 1,
                1
            ) <> '0'
        )
        AND CAST(
            substr(profile_version, 1, instr(profile_version, '.') - 1)
            AS INTEGER
        ) BETWEEN 0 AND 2147483647
        AND CAST(
            substr(
                profile_version,
                instr(profile_version, '.') + 1,
                instr(substr(profile_version, instr(profile_version, '.') + 1), '.') - 1
            ) AS INTEGER
        ) BETWEEN 0 AND 2147483647
        AND CAST(
            substr(
                profile_version,
                instr(profile_version, '.')
                    + instr(substr(profile_version, instr(profile_version, '.') + 1), '.')
                    + 1
            ) AS INTEGER
        ) BETWEEN 0 AND 2147483647
    ),
    profile_digest TEXT NOT NULL CHECK (
        length(profile_digest) = 64
        AND profile_digest = lower(profile_digest)
        AND profile_digest NOT GLOB '*[^0-9a-f]*'
    ),
    profile_payload BLOB NOT NULL CHECK (
        length(profile_payload) BETWEEN 1 AND 1048576
    ),
    PRIMARY KEY (profile_id, profile_version),
    UNIQUE (profile_id, profile_version, profile_digest)
) STRICT;

CREATE TABLE default_settings_global (
    singleton_id INTEGER NOT NULL PRIMARY KEY CHECK (singleton_id = 1),
    profile_id TEXT NOT NULL,
    profile_version TEXT NOT NULL,
    profile_digest TEXT NOT NULL CHECK (
        length(profile_digest) = 64
        AND profile_digest = lower(profile_digest)
        AND profile_digest NOT GLOB '*[^0-9a-f]*'
    ),
    snapshot_digest TEXT NOT NULL CHECK (
        length(snapshot_digest) = 64
        AND snapshot_digest = lower(snapshot_digest)
        AND snapshot_digest NOT GLOB '*[^0-9a-f]*'
    ),
    snapshot_payload BLOB NOT NULL CHECK (
        length(snapshot_payload) BETWEEN 1 AND 1048576
    ),
    revision INTEGER NOT NULL CHECK (revision >= 0),
    updated_at TEXT NOT NULL CHECK (
        length(updated_at) > 0
        AND trim(updated_at) = updated_at
    ),
    FOREIGN KEY (profile_id, profile_version, profile_digest)
        REFERENCES default_settings_profiles (
            profile_id,
            profile_version,
            profile_digest
        )
        ON UPDATE RESTRICT ON DELETE RESTRICT
) STRICT;

CREATE TABLE project_default_settings (
    project_id TEXT NOT NULL PRIMARY KEY CHECK (
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
    profile_id TEXT NOT NULL,
    profile_version TEXT NOT NULL,
    profile_digest TEXT NOT NULL CHECK (
        length(profile_digest) = 64
        AND profile_digest = lower(profile_digest)
        AND profile_digest NOT GLOB '*[^0-9a-f]*'
    ),
    initial_snapshot_digest TEXT NOT NULL CHECK (
        length(initial_snapshot_digest) = 64
        AND initial_snapshot_digest = lower(initial_snapshot_digest)
        AND initial_snapshot_digest NOT GLOB '*[^0-9a-f]*'
    ),
    initial_snapshot_payload BLOB NOT NULL CHECK (
        length(initial_snapshot_payload) BETWEEN 1 AND 1048576
    ),
    current_snapshot_digest TEXT NOT NULL CHECK (
        length(current_snapshot_digest) = 64
        AND current_snapshot_digest = lower(current_snapshot_digest)
        AND current_snapshot_digest NOT GLOB '*[^0-9a-f]*'
    ),
    current_snapshot_payload BLOB NOT NULL CHECK (
        length(current_snapshot_payload) BETWEEN 1 AND 1048576
    ),
    revision INTEGER NOT NULL CHECK (revision >= 0),
    updated_at TEXT NOT NULL CHECK (
        length(updated_at) > 0
        AND trim(updated_at) = updated_at
    ),
    FOREIGN KEY (project_id) REFERENCES projects (id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    FOREIGN KEY (profile_id, profile_version, profile_digest)
        REFERENCES default_settings_profiles (
            profile_id,
            profile_version,
            profile_digest
        )
        ON UPDATE RESTRICT ON DELETE RESTRICT
) STRICT;

CREATE INDEX idx_project_default_settings_profile
    ON project_default_settings (
        profile_id,
        profile_version,
        profile_digest
    );
