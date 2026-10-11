# Dependencies and provenance

OWY's own code is covered by [LICENSE](LICENSE), [LICENSE-EXCEPTION](LICENSE-EXCEPTION)
and [COPYRIGHT](COPYRIGHT). These terms do not replace the following licences.

- **TokenScope collectors:** MIT, Copyright (c) 2026 Crear12. The original licence
  and source/update provenance are retained in `worker/tokenscope/`. The
  imported files keep their MIT terms; OWY's wrapper/application are AGPL.
- **Python 3.13.9:** Python Software Foundation terms and included third-party
  notices. The build retains the runtime's `python/LICENSE.txt`.
- **.NET 10 / Windows App SDK 2.5.1:** runtime licences and notices are included
  in `licenses/` and the runtime distribution. Source repository:
  <https://github.com/microsoft/WindowsAppSDK>. The named runtime linking
  permission keeps their independent terms in effect.
- **NuGet dependencies:** the exact 16-package inventory, upstream licence
  locations and SHA-256 hashes are in `licenses/nuget-dependencies.json`.
  Identical licence texts share one local copy. This includes the separate
  Windows ML Runtime terms, WebView2's BSD-style licence/notices, and
  System.Numerics.Tensors' MIT licence/third-party notices. BuildTools and
  MSIX tooling are identified as build-only. The build verifies the inventory
  against every locked dependency and refuses missing or altered notices.
  Microsoft runtime agreement requirements are described in
  [THIRD_PARTY_TERMS.md](THIRD_PARTY_TERMS.md); the AGPL linking permission
  does not waive them. Python and .NET runtime terms remain separately listed.
- **AMD ADL:** read-only calls to the user's installed AMD driver. Public interface
  references: <https://github.com/GPUOpen-LibrariesAndSDKs/display-library>.
  No AMD SDK implementation, header, DLL or driver is bundled.
- **NVIDIA NVML:** documented calls to the user's installed NVIDIA driver.
  <https://docs.nvidia.com/deploy/nvml-api/latest/api/index.html>.
  No NVIDIA header, SDK library, DLL or driver is bundled.
- **NVIDIA NVAPI extras:** independent getter declarations for the tested RTX 50
  memory-temperature and voltage layouts. Layout/channel references:
  <https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/Interop/NvApi.cs>
  and <https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/Hardware/Gpu/NvidiaGpu.cs>.
  These reference files are MPL-2.0; upstream notices credit the
  LibreHardwareMonitor contributors and Michael Möller. They supply interface
  IDs/layouts and channel mappings, rather than an imported sensor backend.
  No LHM implementation or NVIDIA SDK library/header is bundled. These
  undocumented getters are not an official compatibility guarantee. NVIDIA's
  public SDK library licence is published at <https://github.com/NVIDIA/nvapi/blob/main/License.txt>;
  it is not treated as permission for every undocumented driver interface.
- **Blade actual-RPM protocol references:**
  <https://github.com/JosuGZ/razer-laptop-control> (GPL-2.0),
  <https://github.com/TimandXiyu/razerblade-cli> (the referenced `razerctld.c`
  declares GPL-2.0-only), and <https://github.com/sqmagellan/razer-ctl>
  (MIT, as identified by its upstream README and repository metadata).
  OWY's C# getter implementation is independent; no upstream Rust/C code or
  Razer software/binaries are imported. Published protocol facts do not
  establish official Razer support or resolve every contractual question.
- **White pixel owl:** AI-generated from an independently generated owl image,
  then converted to ICO. No other artwork is included.
  The project makes no claim of exclusive copyright in solely AI-generated
  pixels. See COPYRIGHT and the release review for rights limits.

The dependency versions/content hashes are pinned in NuGet lockfiles and build
scripts. The repository contains source and notices, not prebuilt vendor
drivers or private diagnostics. A separately negotiated OWY licence does not
waive third-party obligations.
