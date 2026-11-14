# Developer Guide

**Status:** Implemented
**Last Updated:** 2026-11-13
**Audience:** Developers and coding agents
**Last verified:** 2026-11-13 against b1688a8de
**Code:** `global.json`, `Martlet.slnx`, `scripts/Test-Martlet.ps1`, `scripts/Invoke-MartletMcp.ps1`

The [contributor guide](https://github.com/throndir2/Martlet/blob/main/CONTRIBUTING.md) is the source for setup and build commands. [AGENTS.md](https://github.com/throndir2/Martlet/blob/main/AGENTS.md) has the coding agent rules.

## Set up and build

The Windows development setup uses PowerShell 7, Node.js and npm. `global.json` pins .NET SDK 10.0.401. Worker tests use Python 3.12. Docker is optional for Linux-only tests.

Follow the [developer quick start](https://github.com/throndir2/Martlet/blob/main/CONTRIBUTING.md#developer-quick-start) to restore and build the solution. Create a developer profile with `scripts/Test-Martlet.ps1 -InitProfile` when you need validation hosts. The profile is outside the repository and is shared by your worktrees.

## Make and validate a change

Find the owner in the [decision map](https://github.com/throndir2/Martlet/blob/main/docs/DECISION_MAP.md). Use the [glossary](https://github.com/throndir2/Martlet/blob/main/docs/GLOSSARY.md) for names. Add tests for changed behavior and document user-visible changes in the changelog.

Run targeted tests and verify behavior through [Martlet MCP Server](Martlet-MCP-Server). See [Validation and Testing](Validation-and-Testing) for the flow and reporting rules. Keep conversation latency unchanged or lower.

Use a disposable data folder during development. Do not use real credentials or paid services in tests. Follow the [cleanup guide](https://github.com/throndir2/Martlet/blob/main/docs/CLEANUP.md) after stopping the processes you started.
