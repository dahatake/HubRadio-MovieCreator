# Benchmark result schema policy

`schema.json` is the validator for benchmark result envelope version `1.0.0`.

- Within `1.0.0`, payload fields may be added only as optional, backward-compatible evidence.
- A required field removal, type change, enum narrowing, or semantic change requires a new immutable schema file and a new `schemaVersion`.
- Existing raw run JSON is validated with the schema version recorded in that run; historical run files are not rewritten.
- Canonical summaries such as `hardware-inventory.json` and `ffmpeg-capabilities.json` must record the source `benchmarkRunId` and, when available, the raw run SHA-256.
- Raw runs are local evidence and ignored by Git. Canonical summaries contain only reviewed, non-sensitive fields.
- ASR/alignment structural validity, synthetic slot containment, and cross-device agreement do not establish boundary accuracy. Exact timing claims require independently authored timing annotations; otherwise the canonical result must keep accuracy and production thresholds unapproved.
- Local text-model JSON Schema conformance and grounded-ID existence/range/subset checks do not establish that generated wording is supported by the cited tokens. Synthetic control success is not fixture semantic accuracy, prompt-injection robustness, or full FR-006/011/022/025 acceptance. Without independent human references, canonical results must keep semantic groundedness, meaning preservation, answer support, and production thresholds unapproved.
- Manual-render byte identity is a same-machine/GPU/driver observation only. Sampled grayscale MAD establishes local frame correspondence against an offset control, not perceptual quality or all-frame mapping accuracy. Neutral subtitle burn-in does not establish subtitle-to-source-speech or content-level `OriginRef` traceability.
