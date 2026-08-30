# FFmpeg package notice inventory (non-final)

- Task: `PK-003`
- Status: exact development baseline recorded
- Legal approval: **not granted**
- Release approval: **not granted**

HubRadio uses the selected `ffmpeg.exe` and `ffprobe.exe` from the BtbN Windows x64 static LGPL variant identified by `BtbN.FFmpeg.LGPL.8.1` `8.1.2-20260731`. FFmpeg is copyright the FFmpeg developers. The exact build identifier, archive metadata, selected-file hashes, complete configuration line, and source references are in [`manifest.json`](manifest.json).

The observed build configuration contains `--enable-version3` and does not contain `--enable-gpl` or `--enable-nonfree`. The vendor archive's `LICENSE.txt` is the GNU Lesser General Public License Version 3 text; PK-003 measured it as 7,651 bytes with SHA-256 `da7eabb7bafdf7d3ae5e9f223aa5bdc1eece45ac569dc21b3b037520b4464768`. The WinGet catalog separately labels the package `LGPL-2.1`, while FFmpeg's official legal page describes the base project as LGPL 2.1-or-later unless GPL parts are enabled. These observations are evidence, not a legal conclusion.

Corresponding upstream FFmpeg source is identified at commit `9b6c8969e05b4f0b29f0f85cd501be6b3e582e6b`. The vendor build scripts are identified at BtbN/FFmpeg-Builds commit `a99e8230eae00d1cee38f23076a7a1f55cd984e2`, tag `autobuild-2026-07-31-14-10`. Acquisition and verification instructions are in [`README.md`](README.md).

This file is not a complete distribution notice, source offer, SBOM, legal advice, or redistribution approval. The static executables include third-party libraries whose exact source, license text, attribution, vulnerability, and codec/patent review must be reconciled against the actual release candidate by `Q-010`. Do not ship this inventory as a claim of complete compliance.