# Self-contained Python worker runtime

PK-002 uses the official CPython 3.12.10 Windows x64 embeddable ZIP and the PK-001 runtime wheel set. The runtime archive and wheels are build inputs, not tracked repository files. `build-runtime.ps1` never downloads or silently replaces them.

The fixed inputs are recorded in `runtime-manifest.json`:

- official archive: `python-3.12.10-embed-amd64.zip`, 11,133,606 bytes, SHA-256 `4acbed6dd1c744b0376e3b1cf57ce906f9dc9e95e68824584c8099a63025a3c3`;
- 25 runtime wheels from `src/HubRadio.Worker/wheel-matrix.json`;
- the six tracked `hubradio_worker` source files;
- an isolated `python312._pth` with no `import site`;
- `-I -B` for health and protocol startup.

The build script requires absolute paths to the already-acquired runtime archive, an exact runtime-only wheelhouse, and a CPython 3.12 Windows x64 build interpreter. It verifies every input, expands into a unique staging directory below `artifacts/python`, removes generated console launchers and their `RECORD` references, writes a SHA-256 manifest for every payload file, marks the payload read-only, and performs a non-overwriting directory move.

`verify-runtime.ps1` clears the child environment, sets an empty `PATH`, redirects writable cache/home/temp variables to a supplied managed workspace, imports the six native/runtime packages, verifies Python isolation flags and versions, executes health and strict NDJSON ping flows, and rehashes all 2,695 payload files before and after execution.

The measured same-host payload is 268,442,311 bytes with artifact-set SHA-256 `1ef1e9fca2b89c006f6030de0b315a9255c9b969fdf3ac7cc36a30aeb412ab7f`. Two independent builds from the same fixed inputs produced this value. This is PK-002 development evidence, not a clean-machine or installed-MSIX result.

The generated runtime remains under ignored `artifacts/`. PK-006 must assemble and verify it in the final package on a clean Windows target. Q-010 must complete notices, vulnerabilities, SBOM, legal review, and release approval. CUDA and every held or condition-false model/runtime remain excluded.