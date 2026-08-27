# Third-party notice inventory (non-final)

- Status: F-008 BOM scaffold
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
| React 19.2.8 | `package.json` plus exact `package-lock.json` entry | MIT declared by lock | Obtain artifact SHA-256 and complete the production transitive notice set at PK-005/Q-010. |
| React DOM 19.2.8 | `package.json` plus exact `package-lock.json` entry | MIT declared by lock | Obtain artifact SHA-256 and complete the production transitive notice set at PK-005/Q-010. |
| CPython worker runtime | `requires-python >=3.12`; no exact runtime distribution is selected | Pending PK-002 | Fix the Windows x64 runtime, hashes, bundled components, and notices before self-contained packaging. |

The current worker declares no product runtime Python packages. Stage 0 Python packages listed below are not silently promoted into the worker dependency graph.

## Conditional Stage 1 runtime candidates

| Component | Decision boundary | License indicator | Required closure |
|---|---|---|---|
| BtbN FFmpeg LGPL build, `ffmpeg.exe`, and `ffprobe.exe` | Fixed LGPL build is a development baseline; GPL-enabled builds remain prohibited | LGPL-2.1-or-later base; bundled dependency licenses are not yet complete | PK-003 must fix source availability, exact build configuration, binary hashes, notices, dependency licenses, and codec-patent review. |
| faster-whisper 1.2.1 and CTranslate2 4.8.1 | Conditional local ASR runtime; CPU is the mandatory fallback | MIT evidence is recorded for exact tags | PK-001 must create the adopted hash lock; PK-004 must package the exact model without silent updates; Q-010 must close transitive notices and vulnerabilities. |
| Systran faster-whisper-small exact revision | Conditional ASR model baseline only | Exact-revision model card declares MIT | No model or cache redistribution until PK-004 license, model-card, offline-cache, and integrity gates pass. |
| Foundry Local CUDA execution-provider cache | Local benchmark use only; redistribution approval is false | NVIDIA SDK EULA and component-specific terms remain incomplete | Never copy the observed cache into a release. Approve a separate redistributable runtime or disable CUDA and release CPU-only. |

ADR-0007’s Stage 1 baseline is not a release decision. ASR accuracy thresholds, CUDA redistribution, FFmpeg redistribution, and complete notice assembly remain open release blockers.

## Held, condition-false, and benchmark-only models

| Candidate | Recorded state | Packaging consequence |
|---|---|---|
| Foundry Local Whisper small | Held; timing capability was insufficient | Do not use as the FR-002 timing provider and do not bundle its cache. |
| MediaPipe BlazeFace short-range | Held; positive control passed but fixture detections were 0/300 | Keep manual layout/keyframes; do not add the package/model to the product lock. |
| MediaPipe BlazeFace full-range local cache file | Benchmark-only and unbenchmarked; canonical provenance is missing | Presence in ignored cache is not adoption; source, version, license, model card, and annotation remain PK-004 work. |
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
- npm 11.17.0, TypeScript 7.0.2, Vite 8.2.2, Vitest 4.1.11, and the three direct `@types` packages.
- `setuptools>=77` and pytest 9.0.0 from the worker declaration; neither has a committed Python lock or artifact SHA-256.
- MSTest.Sdk 4.3.3 and its assets-resolved direct test packages: MSTest.TestAdapter 4.3.3, MSTest.TestFramework 4.3.3, Microsoft.Testing.Extensions.CodeCoverage 18.9.0, and Microsoft.Testing.Extensions.TrxReport 2.3.3.
- Microsoft.Windows.SDK.BuildTools 10.0.26100.1 and `makeappx.exe`, used only for the unsigned Stage 0 packaging feasibility run.
- WinApp CLI 0.6.1.0, observed but not used because its implicit BuildTools bootstrap was not reproducible.

Test and build tool licenses still belong in the final engineering SBOM where required, even when they are excluded from the distributable package.

## Other benchmark-only software and host components

- `huggingface_hub` 1.28.0 and `tokenizers` 0.23.1 were observed in the ASR benchmark environment only.
- MediaPipe 1.0.1, PySceneDetect 0.7.1, ONNX Runtime 1.29.0, PyAV 18.1.0, NumPy 2.5.2, OpenCV packages 5.0.0.93, and their measured model assets remain benchmark or held inputs unless a later task explicitly adopts and locks them.
- Both `opencv-python` and `opencv-contrib-python` were installed into the same Stage 0 environment. PK-001 must select one compatible provider and record a real wheel hash rather than treating installed-manifest hashes as wheel hashes.
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
2. npm lock entries currently carry SHA-1 SRI values. These are retained only as lock evidence; artifact SHA-256 values remain `pending-PK-005`.
3. The worker has no adopted Python lock. `setuptools`, pytest, ML wheels, native DLLs, and optional GPU components require PK-001/PK-002 closure.
4. FFmpeg’s archive provenance, executables, configure line, source availability, bundled libraries, and codec obligations must be reconciled into one PK-003 package manifest.
5. Model acquisition, terms acceptance, model cards, exact revisions, file or manifest hashes, cache behavior, and offline startup must pass PK-004. Held and condition-false models must stay out of the release package.
6. The Web production bundle and its transitive licenses require PK-005. WinUI, .NET runtime, Python worker, FFmpeg, Web assets, model files, and notices require PK-006 package verification.
7. PK-007 signing/install/upgrade/uninstall evidence does not exist yet.
8. Q-010 must generate and compare the release-candidate SBOM and Model BOM, review licenses and vulnerabilities, verify notice contents against actual package files, and fail closed on every unresolved item.

Until all applicable blockers are closed by an authorized reviewer, `THIRD-PARTY-NOTICES.md` must remain a non-final inventory and must not be shipped as a claim of complete compliance.
