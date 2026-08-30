# Offline model bundle

`catalog.json` is the closed PK-004 model inventory. Runtime acquisition is disabled; the application may install only the single `runtimeEligible` entry from an already-present offline bundle.

The adopted development bundle is expected at:

`Bundles/faster-whisper-small-536b0662742c02347bc0e980a01041f333bce120/`

It contains exactly the four files, byte sizes, and SHA-256 values listed in `catalog.json`. `HubRadio.App` copies that local directory into `ModelCatalog/Bundles/` when it exists. The bundle bytes remain ignored by Git and must be assembled from the separately verified model artifact; `PK-006` owns the final package assembly evidence.

Held, benchmark-only, and condition-false entries have no `bundle` field and must not be copied, downloaded, cached, or shipped. A cache mismatch is reported as corruption and is never overwritten as a silent upgrade. Release notice, vulnerability, SBOM, and legal approval remain open through `Q-010`.