CREATE TABLE audit_chain_entries (
    sequence_number INTEGER PRIMARY KEY CHECK (sequence_number >= 1),
    audit_event_id TEXT NOT NULL UNIQUE CHECK (
        length(audit_event_id) = 36
        AND audit_event_id = lower(audit_event_id)
        AND substr(audit_event_id, 9, 1) = '-'
        AND substr(audit_event_id, 14, 1) = '-'
        AND substr(audit_event_id, 19, 1) = '-'
        AND substr(audit_event_id, 24, 1) = '-'
        AND length(replace(audit_event_id, '-', '')) = 32
        AND replace(audit_event_id, '-', '') NOT GLOB '*[^0-9a-f]*'
        AND audit_event_id <> '00000000-0000-0000-0000-000000000000'
    ),
    format_version INTEGER NOT NULL CHECK (format_version = 1),
    previous_hash TEXT NOT NULL CHECK (
        length(previous_hash) = 64
        AND previous_hash = lower(previous_hash)
        AND previous_hash NOT GLOB '*[^0-9a-f]*'
    ),
    event_hash TEXT NOT NULL UNIQUE CHECK (
        length(event_hash) = 64
        AND event_hash = lower(event_hash)
        AND event_hash NOT GLOB '*[^0-9a-f]*'
    ),
    FOREIGN KEY (audit_event_id) REFERENCES audit_events (id)
        ON UPDATE RESTRICT ON DELETE RESTRICT
) STRICT;
