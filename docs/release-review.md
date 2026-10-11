# Public-source release review

This document records the review of OWY's public source release.
This is a scoped engineering/provenance review, not a legal clearance opinion.

## Included and excluded

- Included: OWY source/tests, pinned dependency definitions, build/measurement
  scripts, licence notices, generated white pixel owl, and fictional previews.
- Excluded: private settings, histories, Codex configuration/backups, raw
  diagnostics, SSH information, personal deployment notes, old icon artwork,
  build outputs and tools. No proprietary monitoring app patches or vendor
  binaries/drivers are included.
- Public performance evidence selects numeric resource totals only; native
  preview mode opens no hardware or Codex sources.

The runtime reads local Codex logs and can consume loopback telemetry. A log
reader encounters native records while extracting numeric usage; this is not
equivalent to exporting messages. No message content or credentials belong in
history or worker snapshots. Error logs may contain paths and should be
reviewed before attaching them to a public report. This review cannot guarantee
that every future diagnostic is safe to publish.

## Vendor interfaces

No Synapse service/protocol backend is included. Normal sensors use Windows APIs, direct
device queries and installed GPU drivers. The Blade HID actual-fan-speed
implementation is independently written from published protocol references;
no upstream Razer binaries or Linux implementation are copied.

The scoped source comparison found shared protocol/ABI facts and different
implementation structure; it did not identify a concrete copied nontrivial
implementation. That comparison supports, but cannot conclusively prove,
the independence statement. Upstream reference licences are identified
individually in THIRD_PARTY_NOTICES.md, including MIT for `razer-ctl`,
GPL-2.0-only for the referenced `razerctld.c`, and MPL-2.0 for LHM references.

Direct access is not a blanket legal exemption. Razer's
[software EULA](https://www.razer.com/legal/razer-end-user-license-agreement)
restricts reverse engineering of covered software, subject to its stated
exceptions. Whether it applies to a particular independently implemented
device getter depends on the acts, contracts and applicable law. OWY does not
claim written Razer permission or official support.

NVML is documented. The optional RTX 50 memory-temperature/voltage layouts are
undocumented and can change. NVIDIA's public NVAPI SDK libraries have an
[MIT licence](https://github.com/NVIDIA/nvapi/blob/main/License.txt); that does
not establish permission or compatibility for every undocumented interface.
No NVIDIA SDK/header/library or display driver is redistributed. Before
shipping a commercial product, review the applicable interface/driver terms
and any relevant compatibility obligations.

Model names are factual compatibility labels, not endorsement claims. There
are no vendor logos. The tested model is Blade 14 (2025); other Ryzen/NVIDIA
laptops are potential targets, not supported-hardware guarantees.

## Licence and ownership

OWY-specific code uses AGPL-3.0-only with the section 7 permission in
LICENSE-EXCEPTION. Microsoft/.NET/Python runtimes and separately installed
driver libraries retain their terms; the exception does not let downstream
users close covered OWY source. The Windows App SDK's distributable-code
restrictions are preserved in `licenses/`. An installer must retain these
licence files. Source preparation is not a trusted code-signing certificate.

The exact NuGet inventory covers all 16 locked packages and their package
licence/notice hashes, including distinct Windows ML Runtime and WebView2 terms.
The build rejects lockfile/inventory drift and altered or absent notices.
The installer presents a review/acceptance path for the bundled Microsoft
terms before any installation and records a local, numeric/hash-only receipt.
Its agreement is specific to those runtime components; the AGPL permission
continues to govern OWY. These are implementation measures, not a legal opinion
about enforceability in every jurisdiction.

Each build includes OWY-source.zip, a matching source manifest and archive hash.
The build checks the source tree did not change while publishing and verifies
the package before reporting success. External binary redistribution must
retain the matching source archive and licence files, and obtain the required
runtime agreement from recipients. Source-only publication does not ship the
proprietary runtime binaries.

Imported MIT TokenScope files keep their licence/notices. Previously obtained
MIT copies retain their rights. The AGPL does not retroactively revoke them.
Commercial use under AGPL is free; separate paid terms address uses that
cannot satisfy the covered source-sharing duties. See the
[GNU dual-licensing explanation](https://www.gnu.org/licenses/gpl-faq.html#HeardOtherLicense).

Before executing paid licences, verify the maintainer's ownership or control
of copyrightable contributions, any employment/third-party obligations, and
the contract's precise scope. Future contributions need an explicit written
relicensing grant before merge; an ordinary AGPL contribution is insufficient
for a proprietary alternative. No commercial contract or contributor consent
is implied by this repository.

The white pixel owl is AI-generated from an independently generated owl
reference. No other artwork is included. The project does not
claim exclusive copyright in solely AI-generated pixels. The
[U.S. Copyright Office report](https://www.copyright.gov/newsnet/2025/1060.html)
distinguishes human-authored contributions from purely generated material.
Licences grant only rights actually held; AI assistance does not supply a
blanket assurance of copyright ownership or exclusivity.

## Evidence limits

The efficiency figure uses a short AC resource sample, not an electrical-power
comparison. Combined UI/worker CPU was 1.25% of one logical core, slightly above
the original 1% target. No quantitative battery-life improvement is claimed.
Zero recorded NVIDIA process memory is not proof that the GPU was physically
asleep. The sensor pictures use fictional numbers and are labelled as previews.

Before any public binary release: validate signing/deployment on a clean
machine, retain dependency licences, provide the covered Corresponding Source,
and review the paid-licensing ownership/interface questions above. No public
repository, release upload or telemetry upload is created by this preparation.
