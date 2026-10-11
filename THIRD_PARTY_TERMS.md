# Bundled runtime terms

OWY's own code remains AGPL-3.0-only with LICENSE-EXCEPTION. The additional
conditions below concern the separately licensed runtime components, not your
AGPL rights to OWY or rights granted by other open-source licences.

Before installing or using the bundled Microsoft runtime components, read and
agree to their applicable licence terms. The full, unmodified texts accompany
this package:

- Windows App SDK / WinUI: `licenses/microsoft.windowsappsdk-2.5.1-license.txt`.
- Windows ML Runtime: `licenses/microsoft.windows.ai.machinelearning-2.1.74-license.txt`.
- Windows App SDK ML component: `licenses/microsoft.windowsappsdk.ml-2.1.94-license.txt`.

These Microsoft terms cover permitted use and redistribution, protection of
notices and intellectual property, warranty/liability limitations, and their
stated legal exceptions and regional consumer rights. They also require
distributors to obtain the corresponding agreement from external recipients.
OWY's linking exception does not replace these requirements or grant rights
to Microsoft software. Separately licensed open-source portions retain their
own terms, including any rights those terms grant in preference to conflicting
restrictions. See `licenses/nuget-dependencies.json` and THIRD_PARTY_NOTICES.md
for the complete package licence/notice inventory; Python's terms are also in
`python/LICENSE.txt` in binary packages.

Run `install.ps1 -ShowThirdPartyTerms` to print this document and the complete
Microsoft texts. Installation requires typing `ACCEPT`, or an explicit
`-AcceptThirdPartyTerms` argument after reviewing them. Declining stops before
installation, settings changes or launch. Automated deployments must obtain
the recipient's agreement first; the switch is not permission to agree on
someone else's behalf. A local receipt records only the accepted document
hashes and acceptance time; nothing is uploaded.

If you run an executable directly from the package, these runtime terms still
apply. Review them before doing so. Distribute this document, the complete
licence/notice files, and the matching OWY-source.zip with the executable.
The source archive supplies OWY's corresponding source, tests, build scripts,
dependency lockfiles, assets and documentation. It does not relicense or
include the source of separately licensed Microsoft runtimes.
