# FFmpeg package acquisition and build evidence

- Task: `PK-003`
- Package: `BtbN.FFmpeg.LGPL.8.1` `8.1.2-20260731`
- Build identifier: `n8.1.2-34-g9b6c8969e0-20260731`
- Product state: fixed development baseline; **not approved for release or redistribution**

`manifest.json` is the machine-readable source of truth for this package selection. Do not use BtbN's floating `latest` asset and do not substitute a GPL, nonfree, shared, ARM64, or newer build.

## Acquire the vendor archive

Acquire only the exact release asset named in `manifest.json`. Before extraction, require both the expected byte size and SHA-256 to match. PK-003 corroborated the expected archive hash from the exact WinGet installer metadata and the GitHub release asset digest, but the archive bytes were not retained and independently rehashed by PK-003. That limitation must remain visible until a later package build rehashes the acquired archive.

After extraction, preserve the vendor `LICENSE.txt`. Select only `bin/ffmpeg.exe`, `bin/ffprobe.exe`, and the license file for later package assembly; `bin/ffplay.exe` is not a product dependency. Verify every selected file against the byte size and SHA-256 in `manifest.json` before use or packaging.

## Acquire corresponding source

The FFmpeg version identifier resolves to upstream commit `9b6c8969e05b4f0b29f0f85cd501be6b3e582e6b` on the vendor-selected `release/8.1` branch. Acquire that exact commit from `https://github.com/FFmpeg/FFmpeg.git` or the commit archive URL recorded in `manifest.json`; a moving branch head is not corresponding-source evidence.

The build recipe is BtbN/FFmpeg-Builds tag `autobuild-2026-07-31-14-10`, commit `a99e8230eae00d1cee38f23076a7a1f55cd984e2`. Its scripts are the source for dependency selection, patches, target flags, package layout, and the vendor build mechanics. A release source archive for that tag does not replace the corresponding FFmpeg and bundled-dependency sources.

## Vendor build mechanics

At the exact BtbN build-repository commit, the documented vendor invocations are:

1. `./makeimage.sh win64 lgpl 8.1`
2. `./build.sh win64 lgpl 8.1`

The `8.1` addin selects `GIT_BRANCH=release/8.1`; the `lgpl` defaults select `--enable-version3`, omit `--enable-gpl` and `--enable-nonfree`, and copy `COPYING.LGPLv3` as `LICENSE.txt`. The static Windows packaging script copies executables, documentation, and presets into the release archive.

Those unmodified commands clone the moving `release/8.1` branch. Running them later therefore does **not** prove an exact rebuild of the recorded artifact. A controlled rebuild must detach the cloned FFmpeg tree at commit `9b6c8969e05b4f0b29f0f85cd501be6b3e582e6b` before configuration, retain the exact build-repository commit and dependency inputs, and then compare the produced version line, complete configuration line, selected file hashes, and notices. PK-003 did not perform or claim that rebuild.

## Verification boundary

Require all of the following before accepting an acquired or rebuilt binary:

- archive and selected-file byte sizes and SHA-256 values match `manifest.json`;
- `ffmpeg -version` contains the exact build identifier;
- the complete configuration line equals `versionEvidence.configuration` in `manifest.json`;
- `--enable-version3` is present, while `--enable-gpl` and `--enable-nonfree` are absent;
- the required filters and encoders remain those recorded by `benchmarks/results/ffmpeg-capabilities.json`;
- the bundled `LICENSE.txt` hash and title match the manifest.

Listing an encoder or hardware-acceleration method is not evidence that runtime encoding succeeded. PK-003 does not upgrade the Stage 0 `runtimeEncodeSuccessMeasured=false` result.

## Remaining release work

The static executables incorporate multiple third-party libraries. PK-003 fixes identity, source references, build configuration, selected binary hashes, and the observed license file; it does not complete every dependency license/source, codec or patent review, vulnerability review, release-candidate SBOM, legal approval, or release approval. `PK-006` must verify the actual packaged bytes, and `Q-010` must close all applicable supply-chain and notice gates before distribution.