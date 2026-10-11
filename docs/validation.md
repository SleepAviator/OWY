# Source preparation validation

Checked on Windows 11 build 26300, Razer Blade 14 (2025), on 2026-10-10.
These validation runs did not publish a repository or upload a binary release.

- Native C# build and self-contained WinUI publish completed without warnings
  or errors. Core test and app restores use the same `win-x64` target; locked
  NuGet restore succeeded.
- C# regressions passed: receipt/history conservation and recovery, all ten
  ranges, IPC bounds, power domains, GPU selection/non-waking guards, EcoQoS,
  resource cleanup, 23 direct NVIDIA checks and 31 direct-device checks.
- Python: **15 OWY wrapper/configuration tests + 126 imported collector tests**
  passed. These cover deduplication, timing, receipt expiry, missing coverage,
  subagents, native logs and side-chat telemetry.
- Native fictional preview exported both popup states and exited successfully.
  It starts neither the Codex worker nor hardware sensors, opens no telemetry
  port, uses temporary numeric history and disables configuration controls.
- The pre-existing installed OWY process and its loopback telemetry receiver
  remained running during the captures. No Codex restart or driver installation
  was performed.
- Public source was scanned for private host/user paths, credential patterns,
  configuration, raw histories and obsolete backend/artwork names. Matches in
  privacy tests are deliberately fictional. Upstream notices are preserved.
  README/local-document links and image files were checked.

Release preparation follow-up on 2026-10-10:

- **16 release-packaging checks passed** under Windows PowerShell 5.1. They
  cover changed source during a build, dependency-version drift, altered
  notices, absent corresponding source, unsafe manifest paths, incomplete
  source archives, collector-provenance drift, verification without installation, declined terms and
  minimal consent receipts. Consent tests use fictional documents; they do
  not accept vendor agreements or install an app.
- The existing local package inventory confirmed that WebView2 and Windows
  ML binaries ship. Their exact cached package licences/notices were added,
  together with a hashed inventory of all **16 locked NuGet packages**.
  Identical licence texts share one copy. Build-only SDK tools are distinguished
  from runtime dependencies; the SDK BuildTools package declares a licence URL.
- Two stale collector hashes were corrected to match the existing imported
  source and tests, with the previous values retained in PROVENANCE.json.
  The imported collector code and its MIT licence are unchanged.
- Public builds now package a verified corresponding-source archive and the
  full notice inventory. Output folders must be empty, preserving earlier
  packages. `install.ps1 -VerifyOnly` does not install, launch or record consent.
- Transfer-only macOS metadata is excluded from source snapshots. Test fixtures
  are created in a unique temporary directory and removed after the checks.

Run the release checks with `tests/ReleasePackaging.Tests.ps1`. They exercise
the packaging rules without starting sensor readers or a Codex worker.

The efficiency measurement is separate, earlier evidence from the direct-sensor
build. It is a short AC CPU/memory sample, not a measured battery-energy change.
The preview screenshots are raster exports of OWY's own native WinUI content,
including scrollable content, not screenshots of the surrounding desktop.
