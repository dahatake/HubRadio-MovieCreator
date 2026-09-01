# Third-party notice inventory (non-final)

- Status: F-008 BOM scaffold with PK-001 through PK-005 evidence updates where completed
- Evidence date: 2026-08-28
- Release approval: **not granted**
- Legal approval: **not granted**

This document is an inventory for later notice assembly. It is not a release notice, a complete SBOM, legal advice, a license grant, or evidence that redistribution has been approved. It intentionally does not copy license text. Exact component rows, artifact or lock evidence, hashes, status, and conditions are maintained in [`dependencies.csv`](dependencies.csv) and [`models.csv`](models.csv).

Any `pending-PK-*`, held, condition-false, benchmark-only, unreviewed host component, missing transitive closure, or unresolved license/CVE blocks release until the applicable PK task and Q-010 close it. Inclusion in this inventory means “known and tracked,” not “approved to ship.”

## Status vocabulary

| Status | Meaning |
|---|---|
| `declared-product` | Directly declared by current product source, but not approved for release by F-008. |
| `declared-dev`, `declared-test`, `resolved-test` | Build or test dependency; not automatically part of the shipped package. |
| `locked-*` / `adopted-*-locked` | Exact wheel and SHA-256 are fixed by PK-001; this is reproducibility evidence, not release or legal approval. |
| `adopted-conditional` | Accepted only as the bounded Stage 1 baseline described by ADR-0007; release conditions remain open. |
| `held` | Do not add to the product dependency graph until stated re-evaluation evidence and a later decision exist. |
| `condition-false` | The implementation condition is false; do not implement or package the candidate. |
| `benchmark-only` | Observed or acquired only for local technical evaluation. |
| `prohibited-not-acquired` | Explicitly rejected and intentionally not acquired. |
| `not-approved-by-F-008` | F-008 records evidence only and never grants release approval. |

## Current direct product declarations

| Component | Current evidence | License indicator | Inventory action |
|---|---|---|---|
| Microsoft Windows App SDK 2.4.0 | App project declaration, NuGet assets resolution, and locally rehashed matching nupkg | Package-specific Microsoft Windows App SDK License Terms; non-SPDX | Preserve applicable component terms and notices; verify actual packaged files at PK-006/Q-010. |
| React 19.2.8 | Exact lock plus PK-005 tarball size/SHA-256 and hashed Vite output | MIT declared by lock and package tarball | Preserve the evidence in `packaging/web/dependency-manifest.json`; complete final notices, advisories, SBOM, and release approval at Q-010. |
| React DOM 19.2.8 | Exact lock plus PK-005 tarball size/SHA-256 and hashed Vite output | MIT declared by lock and package tarball | Preserve the evidence in `packaging/web/dependency-manifest.json`; complete final notices, advisories, SBOM, and release approval at Q-010. |
| Scheduler 0.27.0 | Exact transitive lock plus PK-005 tarball size/SHA-256 and hashed Vite output | MIT declared by lock and package tarball | Keep it in the closed production runtime set; complete final notices, advisories, SBOM, and release approval at Q-010. |
| CPython worker runtime | Official CPython 3.12.10 Windows x64 embeddable ZIP, exact size/SHA-256, 35 entries, selected PSF signatures, and deterministic 2,695-file payload fixed by PK-002 | PSF License Agreement and bundled third-party notices are retained in the official `LICENSE.txt` | Preserve `packaging/python/runtime-manifest.json`; PK-006 must verify the installed package and Q-010 must close complete notices, vulnerabilities, SBOM, legal, and release approval. |
| faster-whisper 1.2.1 | Exact declaration plus PK-001 runtime lock and wheel SHA-256 | MIT evidence for the exact tag | CPU/int8 only; retain SYSTRAN and embedded Silero notices; PK-004 pins the exact offline model while Q-010 retains final notice closure. |
| CTranslate2 4.8.1 | Exact declaration plus PK-001 runtime lock and Windows x64 wheel SHA-256 | MIT evidence for the exact tag | No CUDA runtime is included or approved; complete binary notice review at Q-010. |

An isolated `ProgressRing` compatibility probe observed `Compatible=false` with Windows App SDK 2.4.0 and `Compatible=true` with `2.4.1-experimental`. The product dependency remains 2.4.0; the experimental package was probe-only and is not a declared product or release dependency.

The current worker declares `faster-whisper==1.2.1` and `ctranslate2==4.8.1`. PK-001 fixes their complete Windows x64 / CPython 3.12 dependency closure to 25 runtime wheels and separates four test-only wheels. PK-002 fixes the CPython 3.12.10 embeddable runtime and read-only self-contained worker payload. The exact component filenames, byte sizes, hashes, and adoption exclusions are maintained in `src/HubRadio.Worker/wheel-matrix.json` and `packaging/python/runtime-manifest.json`; this does not approve CUDA, final notices, vulnerabilities, legal compliance, or release.

## Conditional Stage 1 runtime candidates

| Component | Decision boundary | License indicator | Required closure |
|---|---|---|---|
| BtbN FFmpeg LGPL build, `ffmpeg.exe`, and `ffprobe.exe` | PK-003 fixes the exact Windows x64 static LGPL development baseline; GPL/nonfree substitution remains prohibited | `--enable-version3` is present; GPL/nonfree enable flags are absent; bundled `LICENSE.txt` is LGPLv3 text; static dependency review is incomplete | Preserve [`packaging/ffmpeg/manifest.json`](../../../packaging/ffmpeg/manifest.json), its non-final notice inventory, exact source/build references, and selected-file hashes. PK-006 must rehash package inputs; Q-010 must close dependency licenses/sources, codec/patent review, vulnerabilities, SBOM, and final notices. |
| faster-whisper 1.2.1 and CTranslate2 4.8.1 | Conditional local ASR runtime; CPU is the mandatory path | MIT evidence is recorded for exact tags | PK-001 hash lock and PK-004 offline model/cache integrity are complete; Q-010 must close transitive notices and vulnerabilities. |
| Systran faster-whisper-small exact revision | Conditional ASR model baseline only | Exact-revision model card declares MIT | PK-004 pins one offline bundle and rejects network fallback or silent replacement. Final package inclusion and release approval remain PK-006/Q-010 work. |
| Foundry Local CUDA execution-provider cache | Local benchmark use only; redistribution approval is false | NVIDIA SDK EULA and component-specific terms remain incomplete | Never copy the observed cache into a release. Approve a separate redistributable runtime or disable CUDA and release CPU-only. |

ADR-0007’s Stage 1 baseline is not a release decision. ASR accuracy thresholds, CUDA redistribution, FFmpeg redistribution, and complete notice assembly remain open release blockers.

### PK-003 FFmpeg package evidence

PK-003 reconciles the Stage 0 capability record, exact WinGet metadata, GitHub release asset metadata, the installed expanded files, upstream source identity, and runtime verifier constants into [`packaging/ffmpeg/manifest.json`](../../../packaging/ffmpeg/manifest.json). The selected `ffmpeg.exe`, `ffprobe.exe`, and `LICENSE.txt` were locally rehashed; their sizes and hashes match the manifest. The complete observed `-buildconf` line is preserved without inventing `--disable-gpl` or `--disable-nonfree` flags. Its actual `--enable-version3` flag, absence of `--enable-gpl`/`--enable-nonfree`, and the LGPLv3 license text copied by the vendor recipe are recorded separately from the WinGet catalog's `LGPL-2.1` label.

The expected vendor archive size and SHA-256 agree between the exact WinGet installer metadata and the GitHub release asset digest, but PK-003 did not retain and independently rehash the archive bytes. The upstream FFmpeg commit and BtbN build-repository commit are fixed, while a bit-for-bit rebuild and complete static dependency source/license closure are not claimed. [`packaging/ffmpeg/NOTICE.md`](../../../packaging/ffmpeg/NOTICE.md) is explicitly non-final. Legal approval, release approval, corresponding-source bundle assembly, complete dependency notices, codec/patent review, vulnerability review, and SBOM remain open for PK-006/Q-010.

## Held, condition-false, and benchmark-only models

| Candidate | Recorded state | Packaging consequence |
|---|---|---|
| Foundry Local Whisper small | Held; timing capability was insufficient | Do not use as the FR-002 timing provider and do not bundle its cache. |
| MediaPipe BlazeFace short-range | Held; positive control passed but fixture detections were 0/300 | Keep manual layout/keyframes; do not add the package/model to the product lock. |
| MediaPipe BlazeFace full-range local cache file | Benchmark-only and unbenchmarked; canonical provenance is missing | PK-004 records it as non-runtime with no bundle. Any later adoption requires source, version, license, model card, annotation, and a separate decision. |
| YAMNet | Held behind manual listening and annotation | Do not enable automatic deletion or a production score threshold. |
| Silero VAD | Benchmark-only mechanism evidence | A-018 must select the actual implementation; if distributed, retain both parent-package and Silero notices. |
| Qwen3 0.6B and 4B Foundry Local variants | A-015 condition-false; benchmark cache only | Do not implement the local text client and do not copy or bundle model/EP caches. |
| pyannote Community-1 | Condition-false and not acquired because terms were not accepted | Do not download, implement, cache, or distribute without a new authorized acceptance and exact revision/license/hash evidence. |
| TalkNet, SAM 2, DeepFilterNet | Held and not acquired until later-stage evidence | No artifact or product dependency may be inferred from research references. |

No row in `models.csv` has release approval. Catalog-declared licenses are explicitly weaker evidence than an independent distribution review.

## Development and test inventory

The following are tracked because they affect reproducibility and later SBOM generation, but their presence does not mean they ship:

- .NET SDK 10.0.400 selected by `global.json`.
- Node.js 24.19.0 and PowerShell 7.6.5 were observed on the Stage 0 host but are not pinned by repository artifacts; their controlled-toolchain artifacts and license evidence remain pending.
- npm 11.17.0, TypeScript 7.0.2, Vite 8.2.2, Vitest 4.1.11, `@testing-library/dom` 10.4.1, `@testing-library/user-event` 14.6.1, and the three direct `@types` packages.
- setuptools 84.0.0 and pytest 9.0.0 are exact, hash-locked PK-001 artifacts. pytest and its test-only transitive wheels are excluded from the product runtime lock.
- MSTest.Sdk 4.3.3 and its assets-resolved direct test packages: MSTest.TestAdapter 4.3.3, MSTest.TestFramework 4.3.3, Microsoft.Testing.Extensions.CodeCoverage 18.9.0, and Microsoft.Testing.Extensions.TrxReport 2.3.3.
- Microsoft.Windows.SDK.BuildTools 10.0.26100.1 and `makeappx.exe`, used only for the unsigned Stage 0 packaging feasibility run.
- WinApp CLI 0.6.1.0, observed but not used because its implicit BuildTools bootstrap was not reproducible.

Test and build tool licenses still belong in the final engineering SBOM where required, even when they are excluded from the distributable package.

## Other benchmark-only software and host components

- `huggingface_hub` 1.28.0, `tokenizers` 0.23.1, ONNX Runtime 1.29.0, PyAV 18.1.0, and NumPy 2.5.2 are now hash-locked product transitives because the adopted faster-whisper/CTranslate2 package metadata requires them. Product code still uses local-files-only model loading, CPU/int8, and `vad_filter=False`; dependency presence does not enable network fallback, VAD policy, CUDA, or additional models.
- MediaPipe 1.0.1, PySceneDetect 0.7.1, OpenCV packages 5.0.0.93, and their measured model assets remain benchmark or held inputs and are explicitly absent from the PK-001 lock.
- Both `opencv-python` and `opencv-contrib-python` were installed into the same Stage 0 environment. Their installed-manifest hashes are not wheel hashes; because A-012 remains held, PK-001 selects neither provider and a future adoption requires annotation evidence, a separate decision, and a real wheel hash.
- Microsoft Foundry Local CLI/daemon 0.10.3 were Developer-signed preview binaries observed on one host. They are not declared product dependencies.
- Microsoft.Web.WebView2 1.0.4129.50 was a probe SDK. Evergreen WebView2 Runtime 151.0.4129.107 was a host dependency, not bundled. Runtime bootstrap, update, network policy, terms, and release-candidate regression remain open.
- Segoe UI Bold and Microsoft Haruka Desktop were host components used only by benchmark controls. They were not bundled, and F-008 does not approve font embedding, rendered-output rights, voice redistribution, or generated-control-audio redistribution.

## Third-party benchmark control assets

The following controls were used only for local technical evaluation and are not approved product assets:

| Asset | SHA-256 | Recorded condition |
|---|---|---|
| MediaPipe portrait positive-control image | `a6f11efaa834706db23f275b6115058fa87fc7f14362681e6abe14e82749de3e` | Apache-2.0 notice evidence is recorded in intake; do not include in product media. |
| MediaPipe speech positive-control WAV | `71caf50b8757d6ab9cad5eae4d36669d3c20c225a51660afd7fe0dc44cdb74f6` | Apache-2.0 notice evidence is recorded in intake; do not include in product media. |

BGM, jingles, SFX, logos, images, and documents remain unavailable for product use unless a RightsRecord and evidence reference approve the exact asset. Neutral placeholders are not a substitute for release rights.

## Known incompleteness and release blockers

1. `dependencies.csv` inventories current direct declarations plus specifically evidenced Stage 0 tools. It is not the complete npm, NuGet, Python, native, .NET runtime, or OS-component transitive closure.
2. PK-005 independently rehashes the React, React DOM, and Scheduler production tarballs and records their aggregate set digest. Remaining build/test npm package artifacts still have only lock-level SHA-1 evidence and remain Q-010 engineering-SBOM work.
3. PK-001 provides an adopted 29-wheel hash lock for Windows x64 / CPython 3.12. PK-002 fixes the exact CPython 3.12.10 embeddable runtime and deterministic CPU worker payload, and PK-004 pins the one eligible CPU model bundle and cache behavior. Complete native notices, vulnerabilities, installed-package bytes, and optional GPU decisions remain open for PK-006/Q-010.
4. PK-003 fixes FFmpeg’s expected archive metadata, locally rehashed selected files, configure line, upstream/build-repository commits, source acquisition method, and non-final notice inventory. Archive-byte rehash, exact rebuild proof, corresponding-source bundle assembly, static dependency licenses, codec/patent review, vulnerabilities, final notices, and SBOM remain open for PK-006/Q-010.
5. PK-004 records model acquisition/consent states, model-card evidence, exact revisions, file and manifest hashes, cache behavior, and offline startup. Held and condition-false models have no bundle path. PK-006/Q-010 must still prove the release package contains only the eligible bytes and complete all notice, vulnerability, SBOM, legal, and release gates.
6. PK-005 produces a closed, source-map-free Web asset manifest and verifies the App output bytes. WinUI, .NET runtime, Python worker, FFmpeg, Web assets, model files, and notices still require PK-006 installed-package verification.
7. PK-007 signing/install/upgrade/uninstall evidence does not exist yet.
8. Q-010 must generate and compare the release-candidate SBOM and Model BOM, review licenses and vulnerabilities, verify notice contents against actual package files, and fail closed on every unresolved item.

Until all applicable blockers are closed by an authorized reviewer, `THIRD-PARTY-NOTICES.md` must remain a non-final inventory and must not be shipped as a claim of complete compliance.
