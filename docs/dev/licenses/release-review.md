# OSL-017 release-candidate review

- Review task: `OSL-017`
- Review date: 2026-09-06
- Review basis: the OSL-012 candidate manifest, candidate payload evidence, runtime manifests, `dependencies.csv`, `third-party-intake.md`, `THIRD-PARTY-NOTICES.md`, and the repository security/quality boundaries.
- Decision: **BLOCKED / REVIEW_PENDING**
- Legal approval: **not granted**
- Release approval: **not granted**
- CVE or vulnerability scan: **not performed by this review**

This is a technical release-candidate review, not legal advice, a license grant, a complete SBOM review, a CVE scan, or a release authorization. It distinguishes artifacts observed or assembled for local technical evaluation from artifacts whose general redistribution conditions are still unverified. `NOASSERTION` means that the reviewed evidence does not establish the applicable license or redistribution permission; it is not a statement that no license exists.

## Candidate identity and scope

The reviewed candidate is the OSL-012 bounded local candidate described by [`artifacts/portable/candidate-review-06/Evidence/portable-release.json`](../../../artifacts/portable/candidate-review-06/Evidence/portable-release.json).

| Field | Recorded value | Review meaning |
|---|---|---|
| Candidate version | `0.1.0-osl.1`, x64 | Candidate identity only; not a release version approval |
| Candidate EXE | `App/HubRadio.App.exe`, 2,058,629,690 bytes | Actual known candidate SHA-256: `159161e3426c566cac6ea43bf6faa6587a2c1e192af81339341f83aa340a092e` |
| Release manifest | `Evidence/portable-release.json` | Actual known manifest SHA-256: `1d44ef54dc805227f0438a76816a7de044dcd8ab3da29353ee4e95be369e52be` |
| Candidate contents | `Contents/`, 3,794 files, 2,057,722,173 bytes | Audit evidence; the manifest labels this tree `audit-only-not-for-folder-distribution` |
| Contents artifact set | — | SHA-256 `505aa9a4bfd64da2a3925401a0b36e37b5c63fbace6c9b66664aff903cdd091b` |
| Source reference | `49c5a49ea4b9c0d38841b212f8dea52529bdf945` | Recorded provenance only; `sourceTreeDirty=true` and the candidate is not a clean-source reproducible-build claim |
| Candidate SBOM | SPDX 2.2 manifest; validation result `Success`, 0 validation errors | Format validation only; not proof of complete package, license, native dependency, model, or vulnerability coverage |
| Candidate release state | `releaseApproved=false` | Must remain blocked pending the open gates below |

The candidate contents visibly include .NET/WinAppSDK binaries, WebView2 loader and a Fixed Version WebView2 tree, CPython and Python worker material, the faster-whisper-small model bundle, FFmpeg/ffprobe, Web production assets, license evidence, and native dependencies. The `preparedPayload` portion contains 3,260 files / 1,819,381,920 bytes from OSL-009; the larger `Contents` tree contains 3,794 files / 2,057,722,173 bytes because it also records the self-contained application/runtime and Web assets. The full file list and hashes remain in the candidate payload manifest; this document does not replace that manifest.

## Classification rule

| Classification | Meaning in this review |
|---|---|
| `LOCAL_TECHNICAL_EVALUATION` | Observed or assembled to test local behavior, packaging mechanics, or a bounded internal workflow. It does not authorize copying, external delivery, or general redistribution. |
| `CONDITIONAL_CANDIDATE` | A candidate input has version/hash evidence and a stated intended package boundary, but one or more license, source, notice, dependency, vulnerability, or release gates remain open. |
| `GENERAL_REDISTRIBUTION` | Not established by OSL-017. This status requires verified terms for the exact bytes, complete applicable notices and sources, package-byte reconciliation, vulnerability review, and authorized release approval. |
| `REVIEW_PENDING` / `NOASSERTION` | The available evidence is insufficient to conclude the license, complete dependency terms, or redistribution right. Do not infer permission from a package label, signature, catalog entry, or local execution. |

The reviewed candidate is therefore `CONDITIONAL_CANDIDATE` for bounded local technical evaluation only. It is **not** `GENERAL_REDISTRIBUTION`.

## Component review

| Candidate scope | Exact evidence reviewed | Technical-evaluation classification | Redistribution / notice conclusion |
|---|---|---|---|
| **WebView2 Fixed Version Runtime** | Version `152.0.4191.62`, x64; CAB SHA-256 `2dc7d817ddce4d036f33684425c218f1ca2d073911136e5d3f8ce9b2340569b5`; 548-file tree digest `fddf4760e18cde307c62c0385024dda51c8a6f8719cf2fb091cdf44778c8c99a`; selected Microsoft signatures only; official license modal snapshot SHA-256 `15bc46c641bc439514b3f0fe5290e0a5ef906bd8106030b53e1e8a142d6fc9d8` | `CONDITIONAL_CANDIDATE` for app-local technical evaluation | `REVIEW_PENDING`. The recorded terms describe conditional object-code redistribution, significant primary functionality, protective distributor/end-user terms, direct Microsoft acquisition evidence, indemnification, notice retention, SmartScreen-related notice obligations, and no standalone runtime/download shortcut. The candidate must not be treated as generally redistributable until those conditions and the exact package assembly are reviewed and accepted. |
| **Python / CPython runtime** | CPython `3.12.10` Windows x64 embeddable archive SHA-256 `4acbed6dd1c744b0376e3b1cf57ce906f9dc9e95e68824584c8099a63025a3c3`; 35 archive entries; official `LICENSE.txt` SHA-256 `e502c6b880ff58d614901495a9009c136539cd0b1e2a2abb8fc00b934c203419`; candidate payload includes `Licenses/CPython-LICENSE.txt` | `CONDITIONAL_CANDIDATE` for isolated local worker evaluation | The PSF license and bundled notices are recorded, but installed MSIX/package reconciliation and complete runtime/native notice closure remain pending. `NOASSERTION` for any component not individually covered by the official runtime notice set. No general redistribution approval. |
| **Python wheels** | Adopted CPU lock includes 25 runtime wheels; exact evidence includes faster-whisper `1.2.1`, CTranslate2 `4.8.1`, huggingface_hub `1.28.0`, tokenizers `0.23.1`, onnxruntime `1.29.0`, PyAV `18.1.0`, NumPy `2.5.2`, plus the remaining lock entries in `wheel-matrix.json`; wheel SHA-256 values are recorded in `dependencies.csv` and intake | `CONDITIONAL_CANDIDATE` for local CPU worker evaluation | Direct package license indicators are recorded (MIT, Apache-2.0, BSD-3-Clause, and NumPy's multi-license expression), but complete wheel-internal and native transitive notices are not closed. PyAV bundled FFmpeg composition and NumPy additional notices remain `REVIEW_PENDING`. Test-only wheels, benchmark-only distributions, CUDA cache, and held candidates are not promoted by this review. |
| **FFmpeg / ffprobe native tools** | BtbN LGPL build `8.1.2-20260731`, build identifier `n8.1.2-34-g9b6c8969e0-20260731`; `ffmpeg.exe` SHA-256 `6099366f31293cdc6c283ea44ffb32f07e3139cd0caf6d0db652a7d064d089cb`; `ffprobe.exe` SHA-256 `4c2f730969c9551aec21c5ca07eb73f63bb0920204c9cd6c9a6e7be6be0458d2`; selected `LICENSE.txt` SHA-256 `da7eabb7bafdf7d3ae5e9f223aa5bdc1eece45ac569dc21b3b037520b4464768` | `CONDITIONAL_CANDIDATE` for bounded local media evaluation | The recorded configuration omits GPL/nonfree enable flags, but the archive was not retained and independently rehashed by PK-003; static dependency license/source closure, codec/patent review, corresponding-source bundle, and final package rehash are open. `NOASSERTION` for unreviewed static dependencies. The GPL-enabled alternative remains prohibited. |
| **ASR model** | `Systran/faster-whisper-small`, revision `536b0662742c02347bc0e980a01041f333bce120`; model manifest SHA-256 `4e924b13871cadaa18f24bf888a6388e45d15da56e06b0b90141dc27d96e951f`; candidate bundle contains four model files; exact model-card evidence records MIT | `CONDITIONAL_CANDIDATE` for offline local CPU evaluation | The exact revision and model-card indicator are recorded, and network fallback is disabled by the recorded boundary. This is not an independent grant of model redistribution rights; model-file/package reconciliation, complete notices, and release review remain pending. Foundry Local models and CUDA EP cache are excluded and must not be copied or bundled. |
| **Web assets** | Production dependency manifest artifact set SHA-256 `2f7dd38035e86b41f059d67ced299d2dd5266f2c3bc5d074e17bc2e402d4f565`; React `19.2.8`, React DOM `19.2.8`, Scheduler `0.27.0` tarball SHA-256 values are recorded; candidate contains the hashed Vite output under `WebAssets/` | `CONDITIONAL_CANDIDATE` for the closed local WebView surface | The three production package indicators are MIT and the output is source-map-free/closed according to the manifest. Transitive/build-tool notices, actual installed-package reconciliation, and final package review remain pending. This does not authorize reuse of repository fixtures, fonts, logos, BGM, or other media assets. |
| **.NET / Windows App SDK** | Candidate contains self-contained .NET assemblies and WinAppSDK/WinUI binaries; direct declaration is Microsoft.WindowsAppSDK `2.4.0`, with package hash `6ec2ebb6add33ecebac1f5773ad4cabe934b82fb18d7bea98e011bb0fc0a37b9`; candidate payload has 3,794 files but does not by itself establish component identity for every native file | `CONDITIONAL_CANDIDATE` for local application packaging evaluation | Microsoft Windows App SDK License Terms are non-SPDX and package-specific. The .NET runtime/native component inventory, exact package-to-payload mapping, applicable notices, and redistribution conditions are `REVIEW_PENDING`. The .NET SDK `10.0.400`, BuildTools, and test packages are development/test evidence, not automatically shipped components. |
| **Native binaries and other payload files** | Candidate payload hashes and paths; selected signed evidence exists for some Microsoft/Python/WebView2 files, while the candidate contains many DLLs, WinMDs, PRI/data files, loaders, restart/helper executables, and native dependencies | `LOCAL_TECHNICAL_EVALUATION` pending component closure | Authenticode validity is provenance/integrity evidence for selected files, not a license or vulnerability conclusion. For every native component not mapped to an exact package/license/source/notice, status is `NOASSERTION`/`REVIEW_PENDING`. The current candidate must not be generally redistributed. |

## Exclusions and non-promotions

The following remain outside a general release scope even if observed in local evaluation records:

- Foundry Local Appx, Foundry Local models, and the 14-file CUDA execution-provider cache: benchmark-only or condition-false; redistribution approval is false and NVIDIA/component terms are incomplete.
- MediaPipe, OpenCV, PySceneDetect, YAMNet, held models, and positive-control media: benchmark/held inputs with no product promotion in this review.
- GPL-enabled FFmpeg: prohibited/not acquired.
- Test and build tools, including npm/Node/Vite/TypeScript tooling, MSTest, BuildTools, and `pytest`: engineering or test inventory unless a later package manifest proves otherwise.
- Windows host Evergreen WebView2 and host fonts: host observations, not substitutes for the Fixed Version runtime or a font redistribution review.
- BGM, jingles, SFX, logos, source media, fixtures, and generated control audio: no external rights or redistribution permission is inferred.

## Required closure before any general redistribution

1. Reconcile the exact candidate payload and final package bytes against the release manifest and component manifests; keep the candidate EXE and release manifest digests above as historical evidence.
2. Complete the native dependency inventory for .NET/WinAppSDK, WebView2, FFmpeg static dependencies, PyAV/bundled libraries, Python runtime, loaders, and every other shipped binary/data component.
3. Preserve all applicable copyright, license, third-party notice, source-offer/corresponding-source, and Microsoft WebView2 conditions in the final package and user-facing distribution terms.
4. Verify the exact model and Web asset boundaries; do not infer rights for models or media from package metadata or local execution.
5. Perform the planned release-candidate SBOM/Model BOM and vulnerability review. This document intentionally does not claim a CVE scan or vulnerability-free result.
6. Obtain authorized legal/release decisions for unresolved `NOASSERTION` and `REVIEW_PENDING` items, including WebView2 conditional redistribution requirements, FFmpeg source/dependency obligations, codec/patent questions, and non-SPDX Microsoft terms.
7. Re-run clean Windows, offline startup, signing, install, upgrade/rollback/uninstall, and final package acceptance gates after the candidate is changed.

Until these gates are closed, `releaseApproved=false` is the only valid release state. The existing [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) remains a non-final inventory and must not be presented as a complete release notice.
