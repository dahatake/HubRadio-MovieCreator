CREATE UNIQUE INDEX idx_artifacts_evaluation_binding
    ON artifacts (id, project_id, content_digest);

CREATE TABLE automated_evaluations (
    evaluation_id TEXT NOT NULL PRIMARY KEY CHECK (
        length(evaluation_id) = 36
        AND evaluation_id = lower(evaluation_id)
        AND substr(evaluation_id, 9, 1) = '-'
        AND substr(evaluation_id, 14, 1) = '-'
        AND substr(evaluation_id, 19, 1) = '-'
        AND substr(evaluation_id, 24, 1) = '-'
        AND length(replace(evaluation_id, '-', '')) = 32
        AND replace(evaluation_id, '-', '') NOT GLOB '*[^0-9a-f]*'
        AND evaluation_id <> '00000000-0000-0000-0000-000000000000'
    ),
    job_id TEXT CHECK (
        job_id IS NULL
        OR (
            length(job_id) = 36
            AND job_id = lower(job_id)
            AND substr(job_id, 9, 1) = '-'
            AND substr(job_id, 14, 1) = '-'
            AND substr(job_id, 19, 1) = '-'
            AND substr(job_id, 24, 1) = '-'
            AND length(replace(job_id, '-', '')) = 32
            AND replace(job_id, '-', '') NOT GLOB '*[^0-9a-f]*'
            AND job_id <> '00000000-0000-0000-0000-000000000000'
        )
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
    settings_profile_id TEXT NOT NULL CHECK (
        length(settings_profile_id) = 36
        AND settings_profile_id = lower(settings_profile_id)
        AND substr(settings_profile_id, 9, 1) = '-'
        AND substr(settings_profile_id, 14, 1) = '-'
        AND substr(settings_profile_id, 19, 1) = '-'
        AND substr(settings_profile_id, 24, 1) = '-'
        AND length(replace(settings_profile_id, '-', '')) = 32
        AND replace(settings_profile_id, '-', '') NOT GLOB '*[^0-9a-f]*'
        AND settings_profile_id <> '00000000-0000-0000-0000-000000000000'
    ),
    settings_profile_version TEXT NOT NULL CHECK (
        length(settings_profile_version) BETWEEN 5 AND 32
        AND settings_profile_version NOT GLOB '*[^0-9.]*'
        AND length(settings_profile_version)
            - length(replace(settings_profile_version, '.', '')) = 2
        AND instr(settings_profile_version, '.') BETWEEN 2 AND 11
        AND instr(
            substr(settings_profile_version, instr(settings_profile_version, '.') + 1),
            '.'
        ) BETWEEN 2 AND 11
        AND length(settings_profile_version)
            - instr(settings_profile_version, '.')
            - instr(
                substr(settings_profile_version, instr(settings_profile_version, '.') + 1),
                '.'
            ) BETWEEN 1 AND 10
        AND (
            instr(settings_profile_version, '.') = 2
            OR substr(settings_profile_version, 1, 1) <> '0'
        )
        AND (
            instr(
                substr(settings_profile_version, instr(settings_profile_version, '.') + 1),
                '.'
            ) = 2
            OR substr(settings_profile_version, instr(settings_profile_version, '.') + 1, 1) <> '0'
        )
        AND (
            length(settings_profile_version)
                - instr(settings_profile_version, '.')
                - instr(
                    substr(settings_profile_version, instr(settings_profile_version, '.') + 1),
                    '.'
                ) = 1
            OR substr(
                settings_profile_version,
                instr(settings_profile_version, '.')
                    + instr(
                        substr(settings_profile_version, instr(settings_profile_version, '.') + 1),
                        '.'
                    )
                    + 1,
                1
            ) <> '0'
        )
        AND CAST(
            substr(settings_profile_version, 1, instr(settings_profile_version, '.') - 1)
            AS INTEGER
        ) BETWEEN 0 AND 2147483647
        AND CAST(
            substr(
                settings_profile_version,
                instr(settings_profile_version, '.') + 1,
                instr(
                    substr(settings_profile_version, instr(settings_profile_version, '.') + 1),
                    '.'
                ) - 1
            ) AS INTEGER
        ) BETWEEN 0 AND 2147483647
        AND CAST(
            substr(
                settings_profile_version,
                instr(settings_profile_version, '.')
                    + instr(
                        substr(settings_profile_version, instr(settings_profile_version, '.') + 1),
                        '.'
                    )
                    + 1
            ) AS INTEGER
        ) BETWEEN 0 AND 2147483647
    ),
    settings_profile_digest TEXT NOT NULL CHECK (
        length(settings_profile_digest) = 64
        AND settings_profile_digest = lower(settings_profile_digest)
        AND settings_profile_digest NOT GLOB '*[^0-9a-f]*'
    ),
    settings_snapshot_digest TEXT NOT NULL CHECK (
        length(settings_snapshot_digest) = 64
        AND settings_snapshot_digest = lower(settings_snapshot_digest)
        AND settings_snapshot_digest NOT GLOB '*[^0-9a-f]*'
    ),
    rule_set_version TEXT NOT NULL CHECK (
        length(rule_set_version) BETWEEN 5 AND 32
        AND rule_set_version NOT GLOB '*[^0-9.]*'
        AND length(rule_set_version) - length(replace(rule_set_version, '.', '')) = 2
        AND instr(rule_set_version, '.') BETWEEN 2 AND 11
        AND instr(substr(rule_set_version, instr(rule_set_version, '.') + 1), '.') BETWEEN 2 AND 11
        AND length(rule_set_version)
            - instr(rule_set_version, '.')
            - instr(substr(rule_set_version, instr(rule_set_version, '.') + 1), '.')
            BETWEEN 1 AND 10
        AND (instr(rule_set_version, '.') = 2 OR substr(rule_set_version, 1, 1) <> '0')
        AND (
            instr(substr(rule_set_version, instr(rule_set_version, '.') + 1), '.') = 2
            OR substr(rule_set_version, instr(rule_set_version, '.') + 1, 1) <> '0'
        )
        AND (
            length(rule_set_version)
                - instr(rule_set_version, '.')
                - instr(substr(rule_set_version, instr(rule_set_version, '.') + 1), '.') = 1
            OR substr(
                rule_set_version,
                instr(rule_set_version, '.')
                    + instr(substr(rule_set_version, instr(rule_set_version, '.') + 1), '.')
                    + 1,
                1
            ) <> '0'
        )
        AND CAST(
            substr(rule_set_version, 1, instr(rule_set_version, '.') - 1)
            AS INTEGER
        ) BETWEEN 0 AND 2147483647
        AND CAST(
            substr(
                rule_set_version,
                instr(rule_set_version, '.') + 1,
                instr(substr(rule_set_version, instr(rule_set_version, '.') + 1), '.') - 1
            ) AS INTEGER
        ) BETWEEN 0 AND 2147483647
        AND CAST(
            substr(
                rule_set_version,
                instr(rule_set_version, '.')
                    + instr(substr(rule_set_version, instr(rule_set_version, '.') + 1), '.')
                    + 1
            ) AS INTEGER
        ) BETWEEN 0 AND 2147483647
    ),
    environment_version TEXT NOT NULL CHECK (
        length(environment_version) BETWEEN 1 AND 256
        AND trim(environment_version) = environment_version
        AND instr(environment_version, char(0)) = 0
    ),
    environment_digest TEXT NOT NULL CHECK (
        length(environment_digest) = 64
        AND environment_digest = lower(environment_digest)
        AND environment_digest NOT GLOB '*[^0-9a-f]*'
    ),
    tool_versions_digest TEXT NOT NULL CHECK (
        length(tool_versions_digest) = 64
        AND tool_versions_digest = lower(tool_versions_digest)
        AND tool_versions_digest NOT GLOB '*[^0-9a-f]*'
    ),
    model_versions_digest TEXT CHECK (
        model_versions_digest IS NULL
        OR (
            length(model_versions_digest) = 64
            AND model_versions_digest = lower(model_versions_digest)
            AND model_versions_digest NOT GLOB '*[^0-9a-f]*'
        )
    ),
    template_digest TEXT CHECK (
        template_digest IS NULL
        OR (
            length(template_digest) = 64
            AND template_digest = lower(template_digest)
            AND template_digest NOT GLOB '*[^0-9a-f]*'
        )
    ),
    music_digest TEXT CHECK (
        music_digest IS NULL
        OR (
            length(music_digest) = 64
            AND music_digest = lower(music_digest)
            AND music_digest NOT GLOB '*[^0-9a-f]*'
        )
    ),
    started_at TEXT NOT NULL CHECK (
        length(started_at) = 33
        AND substr(started_at, 5, 1) = '-'
        AND substr(started_at, 8, 1) = '-'
        AND substr(started_at, 11, 1) = 'T'
        AND substr(started_at, 14, 1) = ':'
        AND substr(started_at, 17, 1) = ':'
        AND substr(started_at, 20, 1) = '.'
        AND substr(started_at, 28, 6) = '+00:00'
    ),
    completed_at TEXT NOT NULL CHECK (
        length(completed_at) = 33
        AND substr(completed_at, 5, 1) = '-'
        AND substr(completed_at, 8, 1) = '-'
        AND substr(completed_at, 11, 1) = 'T'
        AND substr(completed_at, 14, 1) = ':'
        AND substr(completed_at, 17, 1) = ':'
        AND substr(completed_at, 20, 1) = '.'
        AND substr(completed_at, 28, 6) = '+00:00'
        AND completed_at >= started_at
    ),
    evaluated_at TEXT NOT NULL CHECK (
        length(evaluated_at) = 33
        AND substr(evaluated_at, 5, 1) = '-'
        AND substr(evaluated_at, 8, 1) = '-'
        AND substr(evaluated_at, 11, 1) = 'T'
        AND substr(evaluated_at, 14, 1) = ':'
        AND substr(evaluated_at, 17, 1) = ':'
        AND substr(evaluated_at, 20, 1) = '.'
        AND substr(evaluated_at, 28, 6) = '+00:00'
        AND evaluated_at >= completed_at
    ),
    input_digest TEXT NOT NULL CHECK (
        length(input_digest) = 64
        AND input_digest = lower(input_digest)
        AND input_digest NOT GLOB '*[^0-9a-f]*'
    ),
    output_digest TEXT CHECK (
        output_digest IS NULL
        OR (
            length(output_digest) = 64
            AND output_digest = lower(output_digest)
            AND output_digest NOT GLOB '*[^0-9a-f]*'
        )
    ),
    aggregate_status TEXT NOT NULL CHECK (
        aggregate_status IN ('PASS', 'WARNING', 'FAIL', 'NOT EVALUATED')
    ),
    pass_count INTEGER NOT NULL CHECK (pass_count BETWEEN 0 AND 27),
    warning_count INTEGER NOT NULL CHECK (warning_count BETWEEN 0 AND 27),
    fail_count INTEGER NOT NULL CHECK (fail_count BETWEEN 0 AND 27),
    not_evaluated_count INTEGER NOT NULL CHECK (not_evaluated_count BETWEEN 9 AND 27),
    canonical_digest TEXT NOT NULL CHECK (
        length(canonical_digest) = 64
        AND canonical_digest = lower(canonical_digest)
        AND canonical_digest NOT GLOB '*[^0-9a-f]*'
    ),
    canonical_payload BLOB NOT NULL CHECK (
        length(canonical_payload) BETWEEN 1 AND 262144
    ),
    CHECK (
        (artifact_id IS NULL AND artifact_digest IS NULL AND output_digest IS NULL)
        OR (
            artifact_id IS NOT NULL
            AND artifact_digest IS NOT NULL
            AND output_digest = artifact_digest
        )
    ),
    CHECK (artifact_id IS NOT NULL OR aggregate_status <> 'PASS'),
    CHECK (pass_count + warning_count + fail_count + not_evaluated_count = 27),
    CHECK (
        (aggregate_status = 'FAIL' AND fail_count > 0)
        OR (
            aggregate_status = 'NOT EVALUATED'
            AND fail_count = 0
            AND not_evaluated_count > 9
        )
        OR (
            aggregate_status = 'WARNING'
            AND fail_count = 0
            AND not_evaluated_count = 9
            AND warning_count > 0
        )
        OR (
            aggregate_status = 'PASS'
            AND fail_count = 0
            AND not_evaluated_count = 9
            AND warning_count = 0
        )
    ),
    UNIQUE (
        evaluation_id,
        project_id,
        composition_version_id,
        composition_version_digest
    ),
    UNIQUE (evaluation_id, artifact_id, artifact_digest),
    FOREIGN KEY (project_id) REFERENCES projects (id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    FOREIGN KEY (job_id, project_id) REFERENCES jobs (id, project_id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    FOREIGN KEY (composition_version_id, project_id, composition_version_digest)
        REFERENCES composition_versions (id, project_id, content_digest)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    FOREIGN KEY (artifact_id, project_id, artifact_digest)
        REFERENCES artifacts (id, project_id, content_digest)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    FOREIGN KEY (settings_profile_id, settings_profile_version, settings_profile_digest)
        REFERENCES default_settings_profiles (
            profile_id,
            profile_version,
            profile_digest
        ) ON UPDATE RESTRICT ON DELETE RESTRICT
) STRICT;

CREATE TRIGGER trg_automated_evaluation_artifact_insert
BEFORE INSERT ON automated_evaluations
WHEN NEW.artifact_id IS NOT NULL
AND NOT EXISTS (
    SELECT 1
    FROM artifacts AS artifact
    WHERE artifact.id = NEW.artifact_id
      AND artifact.project_id = NEW.project_id
      AND artifact.content_digest = NEW.artifact_digest
      AND artifact.kind = 'Render'
)
BEGIN
    SELECT RAISE(ABORT, 'automated evaluation requires an exact Render artifact binding');
END;

CREATE TRIGGER trg_automated_evaluation_artifact_update
BEFORE UPDATE ON automated_evaluations
WHEN NEW.artifact_id IS NOT NULL
AND NOT EXISTS (
    SELECT 1
    FROM artifacts AS artifact
    WHERE artifact.id = NEW.artifact_id
      AND artifact.project_id = NEW.project_id
      AND artifact.content_digest = NEW.artifact_digest
      AND artifact.kind = 'Render'
)
BEGIN
    SELECT RAISE(ABORT, 'automated evaluation requires an exact Render artifact binding');
END;

CREATE TRIGGER trg_artifact_automated_evaluation_kind_update
BEFORE UPDATE OF kind ON artifacts
WHEN NEW.kind <> 'Render'
AND EXISTS (
    SELECT 1
    FROM automated_evaluations AS evaluation
    WHERE evaluation.artifact_id = OLD.id
      AND evaluation.project_id = OLD.project_id
      AND evaluation.artifact_digest = OLD.content_digest
)
BEGIN
    SELECT RAISE(ABORT, 'an automated evaluation artifact kind must remain Render');
END;

CREATE INDEX idx_automated_evaluations_version_newest
    ON automated_evaluations (
        project_id,
        composition_version_id,
        composition_version_digest,
        evaluated_at DESC,
        evaluation_id
    );

CREATE TABLE automated_evaluation_rules (
    evaluation_id TEXT NOT NULL,
    rule_id TEXT NOT NULL CHECK (
        rule_id IN (
            'codec',
            'fullDecode',
            'duration',
            'avSync',
            'subtitlePresence',
            'subtitleReadingSpeed',
            'subtitleLineLength',
            'subtitleLineCount',
            'subtitleTiming',
            'subtitleSafeZone',
            'loudness',
            'truePeak',
            'provenance',
            'settingsBinding',
            'keyboard',
            'focus',
            'contrast',
            'accessibilityTree',
            'subtitleSemanticCompleteness',
            'nonSpeechSoundCoverage',
            'speakerInformationCoverage',
            'mainContentUnderstanding',
            'humanReadability',
            'naturalness',
            'perceivedAudioQuality',
            'emotionalFit',
            'legalCompliance'
        )
    ),
    category TEXT NOT NULL CHECK (category IN ('machine', 'humanLimitation')),
    scope TEXT NOT NULL CHECK (scope IN ('artifact', 'userInterface', 'humanReview')),
    status TEXT NOT NULL CHECK (
        status IN ('PASS', 'WARNING', 'FAIL', 'NOT EVALUATED')
    ),
    reason_code TEXT NOT NULL CHECK (
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
    ),
    evidence_digest TEXT CHECK (
        evidence_digest IS NULL
        OR (
            length(evidence_digest) = 64
            AND evidence_digest = lower(evidence_digest)
            AND evidence_digest NOT GLOB '*[^0-9a-f]*'
        )
    ),
    target_kind TEXT CHECK (
        target_kind IS NULL
        OR target_kind IN ('preview', 'settings', 'evidence', 'editor')
    ),
    target_project_id TEXT CHECK (
        target_project_id IS NULL
        OR (
            length(target_project_id) = 36
            AND target_project_id = lower(target_project_id)
            AND substr(target_project_id, 9, 1) = '-'
            AND substr(target_project_id, 14, 1) = '-'
            AND substr(target_project_id, 19, 1) = '-'
            AND substr(target_project_id, 24, 1) = '-'
            AND length(replace(target_project_id, '-', '')) = 32
            AND replace(target_project_id, '-', '') NOT GLOB '*[^0-9a-f]*'
            AND target_project_id <> '00000000-0000-0000-0000-000000000000'
        )
    ),
    target_composition_version_id TEXT CHECK (
        target_composition_version_id IS NULL
        OR (
            length(target_composition_version_id) = 36
            AND target_composition_version_id = lower(target_composition_version_id)
            AND substr(target_composition_version_id, 9, 1) = '-'
            AND substr(target_composition_version_id, 14, 1) = '-'
            AND substr(target_composition_version_id, 19, 1) = '-'
            AND substr(target_composition_version_id, 24, 1) = '-'
            AND length(replace(target_composition_version_id, '-', '')) = 32
            AND replace(target_composition_version_id, '-', '') NOT GLOB '*[^0-9a-f]*'
            AND target_composition_version_id <> '00000000-0000-0000-0000-000000000000'
        )
    ),
    target_composition_version_digest TEXT CHECK (
        target_composition_version_digest IS NULL
        OR (
            length(target_composition_version_digest) = 64
            AND target_composition_version_digest = lower(target_composition_version_digest)
            AND target_composition_version_digest NOT GLOB '*[^0-9a-f]*'
        )
    ),
    target_artifact_id TEXT CHECK (
        target_artifact_id IS NULL
        OR (
            length(target_artifact_id) = 36
            AND target_artifact_id = lower(target_artifact_id)
            AND substr(target_artifact_id, 9, 1) = '-'
            AND substr(target_artifact_id, 14, 1) = '-'
            AND substr(target_artifact_id, 19, 1) = '-'
            AND substr(target_artifact_id, 24, 1) = '-'
            AND length(replace(target_artifact_id, '-', '')) = 32
            AND replace(target_artifact_id, '-', '') NOT GLOB '*[^0-9a-f]*'
            AND target_artifact_id <> '00000000-0000-0000-0000-000000000000'
        )
    ),
    target_artifact_digest TEXT CHECK (
        target_artifact_digest IS NULL
        OR (
            length(target_artifact_digest) = 64
            AND target_artifact_digest = lower(target_artifact_digest)
            AND target_artifact_digest NOT GLOB '*[^0-9a-f]*'
        )
    ),
    time_basis TEXT CHECK (time_basis IS NULL OR time_basis IN ('source', 'output')),
    time_value INTEGER CHECK (time_value IS NULL OR time_value >= 0),
    time_rate INTEGER CHECK (time_rate IS NULL OR time_rate > 0),
    setting_key TEXT CHECK (
        setting_key IS NULL
        OR (
            length(setting_key) BETWEEN 1 AND 128
            AND setting_key NOT GLOB '*[^a-zA-Z0-9.]*'
            AND substr(setting_key, 1, 1) GLOB '[a-z]'
            AND setting_key NOT LIKE '%..%'
            AND setting_key NOT GLOB '*.[^a-z]*'
            AND substr(setting_key, -1, 1) <> '.'
        )
    ),
    editor_kind TEXT CHECK (
        editor_kind IS NULL
        OR editor_kind IN ('timeline', 'subtitles', 'audio', 'layout', 'metadata')
    ),
    PRIMARY KEY (evaluation_id, rule_id),
    CHECK (
        (
            rule_id IN (
                'codec', 'fullDecode', 'duration', 'avSync', 'subtitlePresence',
                'subtitleReadingSpeed', 'subtitleLineLength', 'subtitleLineCount',
                'subtitleTiming', 'subtitleSafeZone', 'loudness', 'truePeak',
                'provenance', 'settingsBinding'
            )
            AND category = 'machine'
            AND scope = 'artifact'
        )
        OR (
            rule_id IN ('keyboard', 'focus', 'contrast', 'accessibilityTree')
            AND category = 'machine'
            AND scope = 'userInterface'
        )
        OR (
            rule_id IN (
                'subtitleSemanticCompleteness', 'nonSpeechSoundCoverage',
                'speakerInformationCoverage', 'mainContentUnderstanding',
                'humanReadability', 'naturalness', 'perceivedAudioQuality',
                'emotionalFit', 'legalCompliance'
            )
            AND category = 'humanLimitation'
            AND scope = 'humanReview'
        )
    ),
    CHECK (
        (status = 'NOT EVALUATED' AND evidence_digest IS NULL AND target_kind IS NULL)
        OR (status <> 'NOT EVALUATED' AND evidence_digest IS NOT NULL)
    ),
    CHECK (target_kind IS NULL OR status IN ('WARNING', 'FAIL')),
    CHECK ((target_artifact_id IS NULL) = (target_artifact_digest IS NULL)),
    CHECK ((time_value IS NULL) = (time_rate IS NULL)),
    CHECK ((time_basis IS NULL) = (time_value IS NULL)),
    CHECK (
        category <> 'humanLimitation'
        OR (
            status = 'NOT EVALUATED'
            AND reason_code = 'evaluation.human_review_required'
            AND evidence_digest IS NULL
            AND target_kind IS NULL
        )
    ),
    CHECK (
        (
            target_kind IS NULL
            AND target_project_id IS NULL
            AND target_composition_version_id IS NULL
            AND target_composition_version_digest IS NULL
            AND target_artifact_id IS NULL
            AND target_artifact_digest IS NULL
            AND time_basis IS NULL
            AND time_value IS NULL
            AND time_rate IS NULL
            AND setting_key IS NULL
            AND editor_kind IS NULL
        )
        OR (
            target_kind IS NOT NULL
            AND target_project_id IS NOT NULL
            AND target_composition_version_id IS NOT NULL
            AND target_composition_version_digest IS NOT NULL
        )
    ),
    CHECK (
        target_kind IS NULL
        OR (
            target_kind = 'preview'
            AND time_basis IS NOT NULL
            AND time_value IS NOT NULL
            AND setting_key IS NULL
            AND editor_kind IS NULL
        )
        OR (
            target_kind = 'settings'
            AND time_basis IS NULL
            AND time_value IS NULL
            AND setting_key IS NOT NULL
            AND editor_kind IS NULL
        )
        OR (
            target_kind = 'evidence'
            AND time_basis IS NULL
            AND time_value IS NULL
            AND setting_key IS NULL
            AND editor_kind IS NULL
        )
        OR (
            target_kind = 'editor'
            AND time_basis IS NULL
            AND time_value IS NULL
            AND setting_key IS NULL
            AND editor_kind IS NOT NULL
        )
    ),
    FOREIGN KEY (evaluation_id) REFERENCES automated_evaluations (evaluation_id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    FOREIGN KEY (
        evaluation_id,
        target_project_id,
        target_composition_version_id,
        target_composition_version_digest
    ) REFERENCES automated_evaluations (
        evaluation_id,
        project_id,
        composition_version_id,
        composition_version_digest
    ) ON UPDATE RESTRICT ON DELETE RESTRICT,
    FOREIGN KEY (
        evaluation_id,
        target_artifact_id,
        target_artifact_digest
    ) REFERENCES automated_evaluations (
        evaluation_id,
        artifact_id,
        artifact_digest
    ) ON UPDATE RESTRICT ON DELETE RESTRICT
) STRICT;

CREATE TRIGGER trg_automated_evaluation_rule_artifact_insert
BEFORE INSERT ON automated_evaluation_rules
WHEN NEW.scope = 'artifact'
AND NEW.status <> 'NOT EVALUATED'
AND NOT EXISTS (
    SELECT 1
    FROM automated_evaluations AS evaluation
    WHERE evaluation.evaluation_id = NEW.evaluation_id
      AND evaluation.artifact_id IS NOT NULL
      AND evaluation.artifact_digest IS NOT NULL
)
BEGIN
    SELECT RAISE(ABORT, 'artifact-scoped evaluation requires a bound artifact');
END;

CREATE TRIGGER trg_automated_evaluation_rule_artifact_update
BEFORE UPDATE ON automated_evaluation_rules
WHEN NEW.scope = 'artifact'
AND NEW.status <> 'NOT EVALUATED'
AND NOT EXISTS (
    SELECT 1
    FROM automated_evaluations AS evaluation
    WHERE evaluation.evaluation_id = NEW.evaluation_id
      AND evaluation.artifact_id IS NOT NULL
      AND evaluation.artifact_digest IS NOT NULL
)
BEGIN
    SELECT RAISE(ABORT, 'artifact-scoped evaluation requires a bound artifact');
END;

CREATE TRIGGER trg_automated_evaluation_rule_target_insert
BEFORE INSERT ON automated_evaluation_rules
WHEN NEW.target_kind IS NOT NULL
AND NOT EXISTS (
    SELECT 1
    FROM automated_evaluations AS evaluation
    WHERE evaluation.evaluation_id = NEW.evaluation_id
      AND evaluation.project_id = NEW.target_project_id
      AND evaluation.composition_version_id = NEW.target_composition_version_id
      AND evaluation.composition_version_digest = NEW.target_composition_version_digest
      AND evaluation.artifact_id IS NEW.target_artifact_id
      AND evaluation.artifact_digest IS NEW.target_artifact_digest
)
BEGIN
    SELECT RAISE(ABORT, 'automated evaluation target binding mismatch');
END;

CREATE TRIGGER trg_automated_evaluation_rule_target_update
BEFORE UPDATE ON automated_evaluation_rules
WHEN NEW.target_kind IS NOT NULL
AND NOT EXISTS (
    SELECT 1
    FROM automated_evaluations AS evaluation
    WHERE evaluation.evaluation_id = NEW.evaluation_id
      AND evaluation.project_id = NEW.target_project_id
      AND evaluation.composition_version_id = NEW.target_composition_version_id
      AND evaluation.composition_version_digest = NEW.target_composition_version_digest
      AND evaluation.artifact_id IS NEW.target_artifact_id
      AND evaluation.artifact_digest IS NEW.target_artifact_digest
)
BEGIN
    SELECT RAISE(ABORT, 'automated evaluation target binding mismatch');
END;

CREATE TRIGGER trg_automated_evaluation_binding_update
BEFORE UPDATE OF
    project_id,
    composition_version_id,
    composition_version_digest,
    artifact_id,
    artifact_digest
ON automated_evaluations
WHEN EXISTS (
    SELECT 1
    FROM automated_evaluation_rules AS rule
    WHERE rule.evaluation_id = OLD.evaluation_id
      AND rule.target_kind IS NOT NULL
      AND (
          rule.target_project_id IS NOT NEW.project_id
          OR rule.target_composition_version_id IS NOT NEW.composition_version_id
          OR rule.target_composition_version_digest IS NOT NEW.composition_version_digest
          OR rule.target_artifact_id IS NOT NEW.artifact_id
          OR rule.target_artifact_digest IS NOT NEW.artifact_digest
      )
)
BEGIN
    SELECT RAISE(ABORT, 'automated evaluation binding would invalidate a target');
END;

