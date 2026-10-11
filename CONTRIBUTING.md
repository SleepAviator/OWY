# Contributing to OWY

Keep changes focused, preserve missing-data coverage, and test the affected
accounting/hardware boundary. Reports should identify the laptop model, CPU,
GPU, Windows/driver versions and which fields actually work. Do not post
prompts, usage history, credentials, personal paths, raw telemetry or device
serial numbers. A synthetic `--demo` preview is available for screenshots.

OWY uses AGPL-3.0-only with its Windows runtime linking permission and offers a
separate paid licensing route. By contributing, do not assume the AGPL alone
permits the maintainer to commercially relicense your work. Before a substantial
contribution is merged, agree to a separate written grant allowing the
maintainer to sublicense it under the project's open-source and negotiated
commercial licences. Contributors retain ownership unless separately agreed.
No relicensing agreement is automatically signed by opening an issue or PR.

Do not submit third-party implementation code without its licence/provenance.
API and protocol references must distinguish documented interfaces, independently
declared layouts, and copied implementation. Keep vendor binaries out of the
repository. Avoid adding drivers, hardware setters or sensor polling that wakes
an inactive GPU. Run relevant native tests on Windows and the Python suites.
