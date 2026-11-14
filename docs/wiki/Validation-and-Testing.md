# Validation and Testing

**Status:** Implemented
**Last Updated:** 2026-11-13
**Audience:** Developers and coding agents
**Last verified:** 2026-11-13 against b1688a8de
**Code:** `scripts/Test-Martlet.ps1`, `scripts/Invoke-MartletMcp.ps1`, `tests/known-failures.txt`

The [validation guide](https://github.com/throndir2/Martlet/blob/main/docs/VALIDATION.md) is the source for validation and reporting. Documentation-only changes do not need the test runner or MCP verification. Check documentation links when the change requires it.

## Targeted tests

For code changes, run the tests added or changed and the suite that directly covers the changed code. The runner's `-List` option shows the affected projects without building them. Use `-Project` with the exact test project name and `-Filter` for the targeted tests. Use `python:<worker>` for a worker suite.

Do not use the default diff-wide selection or `-All` for routine validation. Build dependent projects when an API change requires it. Fix failures caused by the change. Report unrelated failures with evidence.

The runner writes `artifacts/validation/summary.md` and logs. It uses the developer profile for configured validation hosts. `-NoHosts` disables those hosts; `-RequireAll` fails if a suite is not run. The defaults are two retries and a 30-minute timeout.

## MCP verification

For each feature or behavior change, exercise the actual behavior through [Martlet MCP Server](Martlet-MCP-Server#verify-a-change) on disposable data. Extend the server so the new behavior is reachable and observable. Check the outcome, not only the call result. Report checks that cannot run as **NOT RUN**, with the reason.

## Other checks and reporting

Run scripts, packaging or other changed paths the way they are used when tests do not cover them. Put the runner summary, MCP calls and results, and NOT RUN items in the pull request. Never describe an unrun check as passed.

Run validation on the developer's own machines. Do not dispatch hosted test or validation workflows. The [local-only policy](https://github.com/throndir2/Martlet/blob/main/CONTRIBUTING.md#local-only-validation-policy) gives the release-workflow exception.
