# 初期検証シナリオ（要求定義書 §18）実測記録

**対象:** source-only vertical slice の最終受入素材
**正本要求:** [docs/requirement-definition.md](../../docs/requirement-definition.md) §18

> [!IMPORTANT]
> 本書には**実際に実行して得た測定値だけ**を記録する。未実施・未測定の項目は「未実施」「未測定」と明記し、`PASS` と書かない。

---

## 1. 受入素材の測定（T-02 / 2026-09-21）

### 1.1 使用した測定ツール

| 項目 | 値 |
|---|---|
| 実行ファイル | `artifacts/portable/candidate-review-06/Contents/Tools/ffprobe.exe` |
| 版 | `n8.1.2-34-g9b6c8969e0-20260731`（`ffprobe -version` の出力） |
| SHA-256 | `4c2f730969c9551aec21c5ca07eb73f63bb0920204c9cd6c9a6e7be6be0458d2` |
| 承認記録との一致 | **一致**（[benchmarks/results/ffmpeg-capabilities.json](./ffmpeg-capabilities.json) `executables.ffprobe.sha256` および `byteSize=113330688`） |
| package | `BtbN.FFmpeg.LGPL.8.1` / `8.1.2-20260731` / `win64-lgpl-static` |

**WinGet導入版について（重要）:** `%LOCALAPPDATA%\Microsoft\WinGet\Packages\BtbN.FFmpeg.LGPL.8.1_*\ffmpeg-n8.1.2-50-g1a748fe2cd-win64-lgpl-8.1\bin\ffprobe.exe` は SHA-256 `67176fa62f89f94c3bcd379fd05677a25651569a2eb8880ec2194e62c82be412` であり、**承認記録と不一致**（別ビルド `n8.1.2-50`）。よって承認済み同梱payload側の実行ファイルを使用した。

### 1.2 素材の同定

| 項目 | 値 |
|---|---|
| パス | `sample/Copilot  あなたの毎日の AI コンパニオン.mp4`（`Copilot` の後は半角スペース2個） |
| SHA-256 | `07904c1dd4c482fa495ea8cd3572c98fff4eaf66b41cf89e396e661f3b8c3e9c` |
| byte size | 9,971,886 |
| 要求定義書 §18 の記載との一致 | **一致** |

### 1.3 承認済みffprobeによる再測定値

| 項目 | 実測値 |
|---|---|
| container | `mov,mp4,m4a,3gp,3g2,mj2` |
| duration | `60.093243` 秒 |
| size | `9971886` bytes |
| 映像 codec / profile | `h264` / `High` |
| 解像度 | 1280 × 720 |
| frame rate (`r_frame_rate`) | `24000/1001`（= 23.976 fps） |
| 音声 codec / profile | `aac` / `LC` |
| sample rate / channels | 44,100 Hz / 2ch |

### 1.4 参考実測値（システム版ffprobe）との照合

調査段階で取得した参考値（1280×720 / 23.976fps / AAC-LC 44.1kHz / 60.093243秒 / 9,971,886 bytes）と、上記1.3の承認済みffprobe再測定値は**全項目一致**した。

**ただし、参考実測値（システム版ffprobe）は受入証拠として流用していない。** 受入証拠として採用するのは §1.3 の承認済みffprobeによる再測定値のみである。

### 1.5 未実施の範囲（T-02 時点）

- pipeline の E2E 実行は**未実施**（`T-31` で実施する）
- loudness / True Peak / A/V同期 / 字幕品質 / 知覚品質の測定は**未実施**
- 人手評価は**未実施**
