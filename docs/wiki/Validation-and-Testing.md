# Validation and Testing

Every code change is validated locally before merge: targeted tests plus MCP verification for behavior changes. No remote CI validation.

## Flow

1. Branch from fresh `origin/main`.
2. Use `-List` to see direct coverage.
3. Run targeted tests only.
4. Verify behavior through Martlet MCP.
5. Run scripts/packaging/workers manually when tests do not cover them.
6. Put `artifacts\validation\summary.md`, MCP calls/results and NOT RUN items in the PR.

## Targeted tests

```powershell
.\scripts\Test-Martlet.ps1 -List
.\scripts\Test-Martlet.ps1 -Project <Project>.Tests -Filter 'FullyQualifiedName~<YourTests>'
```

Do not run default diff-wide selection or `-All` unless asked. A product project name without `.Tests` selects dependent suites; avoid it for routine validation.

## Results

Passed is green. Failed blocks unless unrelated and documented. Flaky does not block. Known failures are from `tests\known-failures.txt`. NOT RUN must be reported honestly.

## Hosts

The local developer profile can name Docker or SSH Linux validation hosts. Never store passwords/tokens or use unlisted production/shared machines.

## MCP

Use `Invoke-MartletMcp.ps1` with disposable data. Feature changes should add automation IDs, SafeClicks, SafeValues and/or headless tools so MCP can reach and observe them.

## Local-only policy

Do not create, enable, dispatch or retry remote test/validation workflows. GitHub Actions are only for the minimal release build.

More detail: [Validation](https://github.com/throndir2/Martlet/blob/main/docs/VALIDATION.md), [Contributing policy](https://github.com/throndir2/Martlet/blob/main/CONTRIBUTING.md#local-only-validation-policy), [AGENTS](https://github.com/throndir2/Martlet/blob/main/AGENTS.md#validate-before-merge), [MCP](https://github.com/throndir2/Martlet/blob/main/docs/MCP.md).
