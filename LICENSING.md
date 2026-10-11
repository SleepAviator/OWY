# Licensing OWY

OWY offers an open-source route and a separately negotiated commercial route.
This policy explains them; the [AGPL text](LICENSE), [runtime permission](LICENSE-EXCEPTION),
and any executed commercial agreement determine the legal rights.

## Open-source use

OWY-specific code is **AGPL-3.0-only**, with the narrow Windows runtime linking
permission. Personal and commercial use are allowed without a licence fee.
You may study, modify, redistribute and sell OWY under those terms.

When distributing covered binaries or a covered combined/derivative work,
provide its Corresponding Source and the required licence/notices under the
AGPL. If you modify OWY and let users interact with that modified version over
a network, section 13 requires offering those users its Corresponding Source.
Unchanged OWY use is not automatically a section 13 publication trigger.

Private use and private modifications without distribution or the relevant
network interaction do **not** require publishing code. Source-sharing duties
concern the covered work and eligible recipients; they do not automatically
apply to every independent application on the same computer, and do not
require a public GitHub repository specifically.

Imported TokenScope files remain MIT; their notices are preserved. Other
dependencies retain their terms. The runtime permission lets OWY use the named
Windows/driver/runtime components without imposing AGPL on those components.
It does not permit closing OWY's own covered source. Previously received
MIT-licensed copies retain the rights already granted with those copies.

Binary packages include their matching `OWY-source.zip` and build instructions.
Keep that archive and all licence/notice files available with the binaries.
The installer obtains agreement to separately licensed Microsoft runtime terms;
see [THIRD_PARTY_TERMS.md](THIRD_PARTY_TERMS.md). Those terms do not impose
additional restrictions on OWY's AGPL-covered code. The linking permission
does not waive the runtime vendors' own redistribution requirements.

## Paid licensing

If a planned integration or redistribution requires keeping covered source
private instead of meeting AGPL obligations, contact
[SleepAviator](https://github.com/SleepAviator) to negotiate a separate paid
licence **before** using OWY under that alternative. Commercial use that
already complies with AGPL does not require payment.

No alternative licence is granted by an enquiry, payment without an agreement,
or this README. Price, scope, permitted products/versions, updates, support,
warranties and source-sharing exceptions must be specified in an executed
written agreement. The maintainer can grant only rights they own or control;
third-party terms remain in effect. Pricing is negotiated, not predetermined.

## Contributions and ownership

Contributors must have the right to submit their work. Substantial contributions
require a separate written relicensing grant before merge so that the
maintainer can continue offering the paid alternative. A DCO sign-off or an
AGPL-only contribution alone does not grant proprietary relicensing rights.
The process is documented in [CONTRIBUTING.md](CONTRIBUTING.md).

These documents are release preparation, not a guarantee that all code or
artwork has enforceable exclusive copyright. Ownership, employment obligations,
AI authorship, and a proposed commercial contract should be reviewed before
selling licences. See the scoped [release review](docs/release-review.md).

References: [AGPL v3](https://www.gnu.org/licenses/agpl-3.0.html),
[GNU dual-licensing FAQ](https://www.gnu.org/licenses/gpl-faq.html#HeardOtherLicense),
[GNU private-use FAQ](https://www.gnu.org/licenses/gpl-faq.html#UnreleasedMods).
