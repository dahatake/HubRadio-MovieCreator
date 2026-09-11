# Self-contained Python worker runtime

PK-002 uses the official CPython 3.12.10 Windows x64 embeddable ZIP and the PK-001 runtime wheel set. The runtime archive and wheels are build inputs, not tracked repository files. `build-runtime.ps1` never downloads or silently replaces them.

The fixed inputs are recorded in `runtime-manifest.json`:

- official archive: `python-3.12.10-embed-amd64.zip`, 11,133,606 bytes, SHA-256 `4acbed6dd1c744b0376e3b1cf57ce906f9dc9e95e68824584c8099a63025a3c3`;
- 25 runtime wheels from `src/HubRadio.Worker/wheel-matrix.json`;
- the seven tracked `hubradio_worker` source files;
- an isolated `python312._pth` with no `import site`;
- `-I -B` for health and protocol startup.

The build script requires absolute paths to the already-acquired runtime archive, an exact runtime-only wheelhouse, and a CPython 3.12 Windows x64 build interpreter. It verifies every input, expands into a unique staging directory below `artifacts/python`, removes generated console launchers and their `RECORD` references, writes a SHA-256 manifest for every payload file, marks the payload read-only, and performs a non-overwriting directory move.

`verify-runtime.ps1` clears the child environment, sets an empty `PATH`, redirects writable cache/home/temp variables to a supplied managed workspace, imports the six native/runtime packages, verifies Python isolation flags and versions, executes health and strict NDJSON ping flows, and rehashes every manifest-declared payload file before and after execution.

## Historical PK-002 evidence (superseded by the 2026-09-09 source refresh)

The measured same-host payload was 2,695 files / 268,442,311 bytes with artifact-set SHA-256 `1ef1e9fca2b89c006f6030de0b315a9255c9b969fdf3ac7cc36a30aeb412ab7f`. Two independent builds from the same fixed inputs, including the then-current six worker source files, produced this value. This remains historical PK-002 development evidence, not evidence for the current source tree, a clean-machine result, or an installed-MSIX result.

## Verified current runtime (2026-09-09; publication-path fix)

- Measured current first-party worker source: **7 files / 65,381 bytes**, artifact-set SHA-256 `1a1b1ab2e6ad4ecef67c2bb24b2229a7d18ce9279e3eae7207e2619db7107595`.
- Actually built and verified runtime: `artifacts/python/WorkerRuntime-Live-20260909c`, **2,696 files / 268,462,606 bytes**, artifact-set SHA-256 `eaadb7f2273afdb228b83df3966464703550c27a76a3ae0bb6b5e8539bb50350`. These measured output values match the current `runtime-manifest.json`; they are no longer only expected values derived from an older payload.
- Saved local evidence: [build log](../../artifacts/validation/worker-pathfix-20260909T125711315Z.build.log) and [verification log](../../artifacts/validation/worker-pathfix-20260909T125711315Z.verify.log). Verification passed all **six** native/runtime imports, isolation/version checks, health and NDJSON ping protocol checks with an empty child `PATH` and offline settings. Manifest-declared files were verified read-only and rehashed before and after execution; `SystemPathRequired=False` and `RuntimeTreeModified=False`.

CPython archive and wheel vendor pins are unchanged. The first-party `task_runner.py` publication fix uses a shorter random temporary filename (`.<uuid-hex>.tmp`): the recorded local failure used a 284-character temporary path, while the compact form used 238 characters in the same staging directory. Exclusive creation and no-overwrite publication are retained. This addresses the observed temporary-path failure, not arbitrary Windows long-path support or an OS configuration change.

The [current-source input measurement](../../artifacts/validation/worker-input-measurement-20260909-859af84318c5484586331508a6888696.json) records `vendorPinsChanged=false`; its `builtBytesVerified=false` describes that earlier, pre-build measurement only. The completed build/verification logs above provide the subsequent actual-runtime evidence. They establish one current-source build and its verification, **not a new two-build reproducibility comparison**. The historical two-build evidence above applies only to its old inputs. Local evidence and runtime outputs are ignored and unavailable in a fresh checkout.

## Prepared payload and remaining gates (2026-09-09)

`artifacts/portable/live-payload-20260909c` was prepared using the corrected runtime. Its [completion receipt](../../artifacts/validation/observed-payload-20260909T141021801Z-cf864da8.json) records exit code **0**; the [output log](../../artifacts/validation/observed-payload-20260909T141021801Z-cf864da8.stdout.log) records **3,266 files / 1,823,960,675 bytes**, artifact-set SHA-256 `e047e26b90149da332c07387e36ee86a847c2c62eb5939b96ec41810e9004580`.

This is prepared dependency-payload integrity/read-only/license evidence, not an App publish or final-package acceptance result. See the [font and build-identity contract](../fonts/README.md). App publish with this payload has not yet been performed; App E2E remains pending QC. `releaseApproved` remains `false`. This README update only reads saved evidence; it performs no builds, tests, commands, or new payload hashing and makes no general release claim.

The generated runtime remains under ignored `artifacts/`. PK-006 must assemble and verify it in the final package on a clean Windows target. Q-010 must complete notices, vulnerabilities, SBOM, legal review, and release approval. CUDA and every held or condition-false model/runtime remain excluded.