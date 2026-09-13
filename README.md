# Martlet

**Status: planning only.** Martlet is a proposed voice-first desktop and gaming
companion. There is no application, installer, inference service, or supported
deployment in this repository yet.

The intended first experience is a Windows installer, microphone and speaker
setup, an explicitly selected AI provider, and a working voice conversation
with built-in troubleshooting. Dedicated AI hardware is not required for the
planned API-backed route. API use may cost money and sends the selected data
to the selected provider; a separate fixture demo will not perform AI inference.

Later milestones cover Ubuntu self-hosting, two-host GPU deployments, opt-in
screen understanding and memory, and optional user-supplied avatars. Reliable
voice and installation take priority over avatars.

| Document | Purpose |
| --- | --- |
| [Development plan](DEVELOPMENT_PLAN.md) | Scope, proposed decisions, priorities, risks, and reading order |
| [Architecture and provider contracts](docs/ARCHITECTURE.md) | Components, trust boundaries, conversation policy, streaming, and failure behavior |
| [Installation and support design](docs/INSTALLATION_SUPPORT.md) | First run, host setup, lifecycle, doctor, and troubleshooting matrix |
| [Delivery and release plan](docs/DELIVERY.md) | PR-sized backlog, dependencies, acceptance criteria, release gates, and traceability |
| [Research and provenance](docs/RESEARCH.md) | Dated primary sources, verified constraints, and unresolved integration questions |

These documents describe **proposals and future acceptance criteria, not
completed features or test results**. No setup commands need to be run to read
the plan. The repository does not yet have a project license; selecting one is
an explicit owner decision before distributing software.
