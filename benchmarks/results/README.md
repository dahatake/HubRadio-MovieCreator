# Benchmark result schema policy

`schema.json` is the validator for benchmark result envelope version `1.0.0`.

- Within `1.0.0`, payload fields may be added only as optional, backward-compatible evidence.
- A required field removal, type change, enum narrowing, or semantic change requires a new immutable schema file and a new `schemaVersion`.
- Existing raw run JSON is validated with the schema version recorded in that run; historical run files are not rewritten.
- Canonical summaries such as `hardware-inventory.json` and `ffmpeg-capabilities.json` must record the source `benchmarkRunId` and, when available, the raw run SHA-256.
- Raw runs are local evidence and ignored by Git. Canonical summaries contain only reviewed, non-sensitive fields.
