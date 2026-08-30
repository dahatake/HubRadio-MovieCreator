# HubRadio MSIX packaging

`HubRadio.Package.wapproj` is the Windows Application Packaging Project for the x64 desktop package. It declares the WinUI entry point and the self-contained worker, fixed FFmpeg tools, CPU/int8 model, license material, and component evidence as one package boundary.

The WAP project requires Visual Studio with the MSIX Packaging Tools component. It is intentionally not part of `HubRadio.slnx`, so the SDK-only restore, build, and test path remains usable on hosts without the DesktopBridge targets. An explicit WAP build must provide these fixed and independently verified input properties:

- `HubRadioPythonRuntimePath`
- `HubRadioModelBundlePath`
- `HubRadioFfmpegPath`
- `HubRadioFfprobePath`
- `HubRadioFfmpegLicensePath`

`build-package.ps1` is the same-host fallback used when the WAP workload is unavailable. It verifies every fixed input, builds the complete self-contained WinUI output, excludes symbols and mutable caches, creates `Evidence/package-layout.json`, and invokes the pinned Microsoft MakeAppx binary without disabling semantic validation.

`verify-package.ps1` unpacks the unsigned MSIX and verifies the complete payload against its layout manifest and SHA-256 block map. It then runs the isolated worker health/protocol checks, loads the packaged CPU/int8 ASR model offline, checks FFmpeg and ffprobe, verifies the closed Web bundle, and runs the WinUI/WebView2 startup smoke test without changing the unpacked bytes.

This is PK-006 development evidence only. It does not claim package signing, trusted installation, clean-machine validation, upgrade, rollback, uninstall, legal review, SBOM completion, or release approval. Those gates remain external and are deliberately reported as false.

## Measured same-host evidence

- Two independent builds produced the same 3,266-file, 1,224,383,084-byte payload set with SHA-256 `b15f5b967f900fa1ce65539cc56733016ad3eb9e42d3e54fe50f3c5335928e73`.
- The outer MSIX hashes differed because MakeAppx recorded container metadata; no outer-byte reproducibility claim is made.
- Strict unpack verification passed for the complete payload, SHA-256 block map, manifest, fixed component evidence, isolated worker health/protocol, FFmpeg/ffprobe, offline CPU/int8 model load, closed Web bundle, and WinUI/WebView2 startup/recovery.
- `dotnet publish` omitted `App.xbf`, `MainWindow.xbf`, and `HubRadio.App.pri`; the fallback therefore uses the complete self-contained `dotnet build` output consumed by the packaging boundary.
- The package is unsigned. `InstalledMsixValidated`, `CleanMachineValidated`, and `ReleaseApproved` remain false.