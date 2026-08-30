CREATE TABLE term_entries (
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
    project_id TEXT CHECK (
        project_id IS NULL
        OR (
            length(project_id) = 36
            AND project_id = lower(project_id)
            AND substr(project_id, 9, 1) = '-'
            AND substr(project_id, 14, 1) = '-'
            AND substr(project_id, 19, 1) = '-'
            AND substr(project_id, 24, 1) = '-'
            AND length(replace(project_id, '-', '')) = 32
            AND replace(project_id, '-', '') NOT GLOB '*[^0-9a-f]*'
            AND project_id <> '00000000-0000-0000-0000-000000000000'
        )
    ),
    series_id TEXT CHECK (
        series_id IS NULL
        OR (
            length(series_id) = 36
            AND series_id = lower(series_id)
            AND substr(series_id, 9, 1) = '-'
            AND substr(series_id, 14, 1) = '-'
            AND substr(series_id, 19, 1) = '-'
            AND substr(series_id, 24, 1) = '-'
            AND length(replace(series_id, '-', '')) = 32
            AND replace(series_id, '-', '') NOT GLOB '*[^0-9a-f]*'
            AND series_id <> '00000000-0000-0000-0000-000000000000'
        )
    ),
    written_form TEXT NOT NULL CHECK (
        length(written_form) > 0
        AND trim(written_form) = written_form
        AND instr(written_form, char(0)) = 0
    ),
    reading TEXT NOT NULL CHECK (
        length(reading) > 0
        AND trim(reading) = reading
        AND instr(reading, char(0)) = 0
    ),
    version INTEGER NOT NULL CHECK (version >= 0),
    CHECK (
        (project_id IS NOT NULL AND series_id IS NULL)
        OR (project_id IS NULL AND series_id IS NOT NULL)
    ),
    FOREIGN KEY (project_id) REFERENCES projects (id)
        ON UPDATE RESTRICT ON DELETE RESTRICT
) STRICT;

CREATE UNIQUE INDEX idx_term_entries_project_written_form_reading
    ON term_entries (project_id, written_form, reading)
    WHERE project_id IS NOT NULL;

CREATE UNIQUE INDEX idx_term_entries_series_written_form_reading
    ON term_entries (series_id, written_form, reading)
    WHERE series_id IS NOT NULL;