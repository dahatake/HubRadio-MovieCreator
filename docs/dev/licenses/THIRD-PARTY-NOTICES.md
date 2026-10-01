# Third-party notice inventory (non-final)

- Status: F-008 BOM scaffold with PK-001 through PK-005 evidence updates where completed
- Evidence date: 2026-08-28
- Release approval: **not granted**
- Legal approval: **not granted**

This document is an inventory for later notice assembly. It is not a release notice, a complete SBOM, legal advice, a license grant, or evidence that redistribution has been approved. The complete upstream MIT license is retained below for the vendored Fluent UI System Icons subset, together with the complete root OFL-1.1 license for the verified Noto Sans JP Regular input; final notice assembly for other components remains open. Exact component rows, artifact or lock evidence, hashes, status, and conditions are maintained in [`dependencies.csv`](dependencies.csv) and [`models.csv`](models.csv).

Any `pending-PK-*`, held, condition-false, benchmark-only, unreviewed host component, missing transitive closure, or unresolved license/CVE blocks release until the applicable PK task and Q-010 close it. Inclusion in this inventory means “known and tracked,” not “approved to ship.”

## Status vocabulary

| Status | Meaning |
|---|---|
| `declared-product` | Directly declared by current product source, but not approved for release by F-008. |
| `declared-dev`, `declared-test`, `resolved-test` | Build or test dependency; not automatically part of the shipped package. |
| `locked-*` / `adopted-*-locked` | Exact wheel and SHA-256 are fixed by PK-001; this is reproducibility evidence, not release or legal approval. |
| `vendored-commit-pinned` | Selected upstream source assets are copied into product source at an exact commit; source identity and retained license do not grant release or legal approval. |
| `verified-input-release-pending` | Saved primary-input verification records exact source identity, bytes, hashes, and license; it does not establish a rebuilt runtime, accepted package, or legal/release approval. |
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
| Fluent UI System Icons (six 20px regular assets) | UX-015 copies actual SVG paths from commit `5bae3fb7771054c252a54b1d9210e9c03439fa1b` into `AppIcons.tsx`; no npm dependency | Exact-commit MIT license retained in full below | Preserve the source map and notice in the UX-015 section; UX-043 checks the actual portable notice copy; final release review remains Q-010. |
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

- .NET SDK 10.0.401 selected by the repository `global.json`; the Stage 0 benchmark probes remain pinned to .NET SDK 10.0.400 by `benchmarks/probes/global.json`.
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

## OSL-017 candidate release review

OSL-017 currently reviews the bounded OSL-012 candidate described by [`artifacts/portable/candidate-review-06/Evidence/portable-release.json`](../../../artifacts/portable/candidate-review-06/Evidence/portable-release.json). The current candidate EXE SHA-256 is `159161e3426c566cac6ea43bf6faa6587a2c1e192af81339341f83aa340a092e`; the recorded release-manifest SHA-256 is `1d44ef54dc805227f0438a76816a7de044dcd8ab3da29353ee4e95be369e52be`. These are candidate evidence values, not approval identifiers. The manifest records `releaseApproved=false`, `sourceTreeDirty=true`, `scope=bounded-local-candidate`, and an audit-only `Contents` tree. Candidate-review-03 was an earlier candidate attempt retained as historical evidence, not the current OSL-017 subject.

The detailed OSL-017 result is [`release-review.md`](release-review.md). It classifies the candidate as `CONDITIONAL_CANDIDATE` for local technical evaluation only, not `GENERAL_REDISTRIBUTION`. It covers the Fixed Version WebView2 terms, CPython and Python wheels, FFmpeg/ffprobe, the offline ASR model, Web production assets, .NET/Windows App SDK, and native payload binaries. Unmapped or incompletely verified components remain `NOASSERTION` or `REVIEW_PENDING`; selected Authenticode evidence, SPDX format validation, package labels, and local execution do not establish legal approval, redistribution permission, or a CVE/vulnerability result.

This section does not replace, narrow, or grant any existing notice. Foundry Local/CUDA cache, held and benchmark-only models, GPL-enabled FFmpeg, test/build tools, host WebView2/font components, and unapproved media remain excluded from general redistribution. The existing Q-010, PK-006, OSL-020, and OSL-021 closure gates remain open.

## UX-015 Fluent UI System Icons subset

- Upstream: Microsoft, `microsoft/fluentui-system-icons`.
- Exact pin: [commit `5bae3fb7771054c252a54b1d9210e9c03439fa1b`](https://github.com/microsoft/fluentui-system-icons/commit/5bae3fb7771054c252a54b1d9210e9c03439fa1b), confirmed through the official GitHub ref and commit API. This is a commit pin, not an inferred npm version or release tag.
- Retrieval date: 2026-09-07. All six SVG XML files and `LICENSE` were fetched at that commit, not drawn from an image or recreated from memory.
- Local implementation: `src/HubRadio.Web/src/components/AppIcons.tsx`. Only the six regular 20px paths below are copied. Path data and `viewBox="0 0 20 20"` are unchanged; the original path `fill="#212121"` is removed and the shared SVG uses `fill="currentColor"`, configurable width/height (default 20), `aria-hidden="true"`, and `focusable="false"`.
- `AppIcon({ name, size? })` is decorative. Visible Japanese labels belong to the caller. The application name `processing` maps to the actual upstream **Sparkle** icon; it is not an upstream icon named Processing, an animated progress indicator, or evidence of successful processing.
- No npm package, UI kit, icon font, remote HTML, runtime fetch, or raw-HTML SVG insertion is added. The remaining upstream assets are not included.

The Git blob SHA-1 identifiers below were returned by the official GitHub file API and by the fetched file-content metadata. They identify the **original SVG files**, not the modified JSX or a raw-file SHA-256. No independent SHA-256 rehash was performed for this change; the CSV's SHA-256 remains `pending-Q-010`. `AppIcons.test.tsx` includes offline checks that reconstruct the original three-line SVG envelope from each rendered path and compare its Git blob SHA-1; these tests were added but not executed here.

| AppIcon name | Original source path at the pinned commit | Git blob SHA-1 |
|---|---|---|
| `video` | [`assets/Video/SVG/ic_fluent_video_20_regular.svg`](https://raw.githubusercontent.com/microsoft/fluentui-system-icons/5bae3fb7771054c252a54b1d9210e9c03439fa1b/assets/Video/SVG/ic_fluent_video_20_regular.svg) | `04f29c5d88569dcc0efe39e555724d3a25e3acf1` |
| `processing` | [`assets/Sparkle/SVG/ic_fluent_sparkle_20_regular.svg`](https://raw.githubusercontent.com/microsoft/fluentui-system-icons/5bae3fb7771054c252a54b1d9210e9c03439fa1b/assets/Sparkle/SVG/ic_fluent_sparkle_20_regular.svg) | `ba11497b00c12a394dc3d15092b4927b9cade4ba` |
| `play` | [`assets/Play/SVG/ic_fluent_play_20_regular.svg`](https://raw.githubusercontent.com/microsoft/fluentui-system-icons/5bae3fb7771054c252a54b1d9210e9c03439fa1b/assets/Play/SVG/ic_fluent_play_20_regular.svg) | `05a0ec3f98ee72567238bfeee1ffa980553d8b0b` |
| `save` | [`assets/Save/SVG/ic_fluent_save_20_regular.svg`](https://raw.githubusercontent.com/microsoft/fluentui-system-icons/5bae3fb7771054c252a54b1d9210e9c03439fa1b/assets/Save/SVG/ic_fluent_save_20_regular.svg) | `4df2b9d636fdaa2635ebcb825acdde439043442c` |
| `settings` | [`assets/Settings/SVG/ic_fluent_settings_20_regular.svg`](https://raw.githubusercontent.com/microsoft/fluentui-system-icons/5bae3fb7771054c252a54b1d9210e9c03439fa1b/assets/Settings/SVG/ic_fluent_settings_20_regular.svg) | `54752c20fe6c380b1f2b5bcbef8738295e2cc1d0` |
| `arrowLeft` | [`assets/Arrow Left/SVG/ic_fluent_arrow_left_20_regular.svg`](https://raw.githubusercontent.com/microsoft/fluentui-system-icons/5bae3fb7771054c252a54b1d9210e9c03439fa1b/assets/Arrow%20Left/SVG/ic_fluent_arrow_left_20_regular.svg) | `32618604e0ece64de2688e01ce14c2959a25c0c3` |

Preserve the entire license below with this subset. The existing `packaging/portable/prepare-payload.ps1` copies this document to `Licenses/THIRD-PARTY-NOTICES-NONFINAL.md`; checking the updated copy inside an actual payload remains UX-043 integration work. No payload was prepared or verified here, and the historical OSL-017 candidate evidence above is not evidence for these new source assets. Existing release and legal approval gates remain unchanged.

### Fluent UI System Icons — complete upstream MIT license

Source: [`LICENSE` at the same commit](https://raw.githubusercontent.com/microsoft/fluentui-system-icons/5bae3fb7771054c252a54b1d9210e9c03439fa1b/LICENSE). Git blob SHA-1: `bc9c36b28f467f7006c4e5f1be4ca0a1a0b895f3`.

```text
MIT License

Copyright (c) 2020 Microsoft Corporation

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Noto Sans JP Regular 2.004 — verified font input (not approved for release)

- Upstream: official `notofonts/noto-cjk`, tag `Sans2.004`, commit `523d033d6cb47f4a80c58a35753646f5c3608a78`.
- Font source: `Sans/SubsetOTF/JP/NotoSansJP-Regular.otf`; Git blob SHA-1 `4af556abb9cc42e585aab777b01857246a110746`; 4,533,028 bytes; raw-file SHA-256 `dff723ba59d57d136764a04b9b2d03205544f7cd785a711442d6d2d085ac5073`.
- Recorded sfnt names: family `Noto Sans JP`, style `Regular`, version `Version 2.004;hotconv 1.0.118;makeotfexe 2.5.65603`, PostScript name `NotoSansJP-Regular`.
- License source: the same commit's **root `LICENSE`**, not `Sans/LICENSE`; Git blob SHA-1 `d952d62c065f3f35fb83a173496e90b21525aef3`; 4,301 bytes; raw-file SHA-256 `6a73f9541c2de74158c0e7cf6b0a58ef774f5a780bf191f2d7ec9cc53efe2bf2`; SIL Open Font License 1.1 (`OFL-1.1`).
- Saved primary files and verification record: `artifacts/inputs/fonts/verified-2.004-7388bda58dd8/` contains `NotoSansJP-Regular.otf`, `LICENSE`, and `verification.json`. The existing `packaging/fonts/font-manifest.json` already records these measured values and root license path. This documentation update reads the saved evidence and license; it does not reacquire files, recompute hashes, or reparse the font.

Retain the original font, complete license, and applicable copyright notices. OFL-1.1 prohibits selling the font by itself and sets conditions on redistribution, modification, Reserved Font Names, and endorsements; the font remains under OFL-1.1, while the license's font-licensing requirement does not apply to documents created using the font. These recorded terms are not a legal approval or a claim of completed compliance review. The payload layout calls for `Fonts/LICENSE` and `Licenses/NotoSansJP-OFL.txt`; their presence in a rebuilt package is not verified here.

`releaseApproved=false` remains unchanged. Worker rebuild has not yet been executed; no payload preparation, App build, test, or native acceptance is claimed by this documentation update. `Evidence/application-build.json` is only a build identifier, not authenticity or independent distribution approval. UX041/UX043 are outside this update's scope. Historical candidate evidence elsewhere in this document does not validate this font input's integration or close the existing release/legal gates.

### Noto Sans JP — complete upstream root LICENSE

The following is the complete text of the saved root `LICENSE` identified above. Its SHA-256 refers to the original file bytes, not this Markdown transcription.

```text
This Font Software is licensed under the SIL Open Font License,
Version 1.1.

This license is copied below, and is also available with a FAQ at:
http://scripts.sil.org/OFL

-----------------------------------------------------------
SIL OPEN FONT LICENSE Version 1.1 - 26 February 2007
-----------------------------------------------------------

PREAMBLE
The goals of the Open Font License (OFL) are to stimulate worldwide
development of collaborative font projects, to support the font
creation efforts of academic and linguistic communities, and to
provide a free and open framework in which fonts may be shared and
improved in partnership with others.

The OFL allows the licensed fonts to be used, studied, modified and
redistributed freely as long as they are not sold by themselves. The
fonts, including any derivative works, can be bundled, embedded,
redistributed and/or sold with any software provided that any reserved
names are not used by derivative works. The fonts and derivatives,
however, cannot be released under any other type of license. The
requirement for fonts to remain under this license does not apply to
any document created using the fonts or their derivatives.

DEFINITIONS
"Font Software" refers to the set of files released by the Copyright
Holder(s) under this license and clearly marked as such. This may
include source files, build scripts and documentation.

"Reserved Font Name" refers to any names specified as such after the
copyright statement(s).

"Original Version" refers to the collection of Font Software
components as distributed by the Copyright Holder(s).

"Modified Version" refers to any derivative made by adding to,
deleting, or substituting -- in part or in whole -- any of the
components of the Original Version, by changing formats or by porting
the Font Software to a new environment.

"Author" refers to any designer, engineer, programmer, technical
writer or other person who contributed to the Font Software.

PERMISSION & CONDITIONS
Permission is hereby granted, free of charge, to any person obtaining
a copy of the Font Software, to use, study, copy, merge, embed,
modify, redistribute, and sell modified and unmodified copies of the
Font Software, subject to the following conditions:

1) Neither the Font Software nor any of its individual components, in
Original or Modified Versions, may be sold by itself.

2) Original or Modified Versions of the Font Software may be bundled,
redistributed and/or sold with any software, provided that each copy
contains the above copyright notice and this license. These can be
included either as stand-alone text files, human-readable headers or
in the appropriate machine-readable metadata fields within text or
binary files as long as those fields can be easily viewed by the user.

3) No Modified Version of the Font Software may use the Reserved Font
Name(s) unless explicit written permission is granted by the
corresponding Copyright Holder. This restriction only applies to the
primary font name as presented to the users.

4) The name(s) of the Copyright Holder(s) or the Author(s) of the Font
Software shall not be used to promote, endorse or advertise any
Modified Version, except to acknowledge the contribution(s) of the
Copyright Holder(s) and the Author(s) or with their explicit written
permission.

5) The Font Software, modified or unmodified, in part or in whole,
must be distributed entirely under this license, and must not be
distributed under any other license. The requirement for fonts to
remain under this license does not apply to any document created using
the Font Software.

TERMINATION
This license becomes null and void if any of the above conditions are
not met.

DISCLAIMER
THE FONT SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO ANY WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT
OF COPYRIGHT, PATENT, TRADEMARK, OR OTHER RIGHT. IN NO EVENT SHALL THE
COPYRIGHT HOLDER BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
INCLUDING ANY GENERAL, SPECIAL, INDIRECT, INCIDENTAL, OR CONSEQUENTIAL
DAMAGES, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF THE USE OR INABILITY TO USE THE FONT SOFTWARE OR FROM
OTHER DEALINGS IN THE FONT SOFTWARE.
```
