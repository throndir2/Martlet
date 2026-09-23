# Martlet local STT offline package tool

Standalone H07a engineering tool. It is intentionally outside `Martlet.slnx`,
Desktop, Core settings and Windows main packaging.

The tool has no download, URL resolution, compiler execution, whisper launch,
firewall mutation or automatic enablement command. All archive, model, evidence
and notice inputs are caller-supplied absolute local paths.

## Commands

```powershell
dotnet run --project src\Martlet.LocalStt.PackageTool\Martlet.LocalStt.PackageTool.csproj -- `
  import `
  --archive C:\reviewed-input\whisper-bin-x64.zip `
  --model C:\reviewed-input\ggml-base.en.bin `
  --notices C:\reviewed-input\notices `
  --evidence C:\reviewed-input\import-evidence.v1.json `
  --destination C:\private-packages\whisper-candidate
```

Without `--approve-rights`, `import` performs bounded read-only preview and
returns `approval_required` (or `blocked` for incomplete evidence). After
reviewing the exact plan, file pins and notice/rights declarations, repeat the
same command with `--approve-rights --approve-plan <fingerprint-from-preview>`.
The fingerprint must match that exact preview and authorization is consumed once.
Changed source file identity, content, times or destination requires a new preview.

```powershell
dotnet run --project src\Martlet.LocalStt.PackageTool\Martlet.LocalStt.PackageTool.csproj -- `
  inspect --destination C:\private-packages\whisper-candidate

dotnet run --project src\Martlet.LocalStt.PackageTool\Martlet.LocalStt.PackageTool.csproj -- `
  cleanup --parent C:\private-packages

dotnet run --project src\Martlet.LocalStt.PackageTool\Martlet.LocalStt.PackageTool.csproj -- `
  build-plan --facts C:\reviewed-input\source-build-facts.v1.json
```

`cleanup` removes only bounded `.martlet-local-stt-*.pending` directories with
a valid exact owner marker for the current embedded manifest and declared
layout; it preserves unknown or malformed directories. Active imports exclude
competing cleanup. A partial cleanup retains its owner for retry. Crash residue
without a valid owner marker is preserved and needs manual review.
Within the same importer instance, retained directory identity also permits
safe cleanup after an incomplete first owner-marker write. Publication uses
create-only native rename followed by identity/byte re-verification under held
destination locks; errors or cancellation roll back only that owned identity
or report its retained path. Rename alone is not success.
`build-plan` emits canonical JSON and never executes CMake, MSVC or another
process; its source/toolchain inputs are caller assertions, not measured
reproducibility or permission to run a build.

All filesystem commands require Windows x64 and canonical local, non-reparse,
single-link files. No command installs tools, downloads models or sets OS policy.
Help is passive. Exit codes: 0 completed, 2 invalid command/input shape,
3 approval required, 4 rejected/blocked/cleanup failure, 130 canceled.

## Import evidence

The strict `import-evidence.v1.json` binds the embedded manifest SHA-256,
runtime/model repositories and revisions, their redirecting locator
observations, complete archive/model hashes, every selected archive member's
installed name/size/SHA-256, and exact caller-supplied license notices. Each
notice declares its component, SPDX claim, source revision, immutable evidence
URL, byte count, SHA-256 and covered `runtime/...` or `models/...` paths.

No production evidence file is committed because the real release archive,
model and complete component notices have not been acquired or reviewed in
this repository. Synthetic tests generate inert evidence and bytes.

Successful import is still not launch permission. The package remains
`disabled_pending_qualification`. Import outputs `payload_path`; inspection
outputs `bytes_verified_unqualified`, `payload_path`, and false qualification
and launch booleans. Use `destination\payload`, never the envelope root, with
`PhysicalLocalSttPackageVerifier`: that subtree contains exactly `downloads`,
`runtime`, and `models`, while notices/ownership/receipts are siblings outside it.
Evidence covers logical `runtime/...` and `models/...` names; receipt/SBOM
inventory paths include `payload/`.

The public adapter remains closed with `PackageUnqualified`; there is no CLI
override, synthetic-manifest switch or qualification grant. Declared PE import
tables are inspected, not executed; dynamic DLL resolution, full dependency
closure, rights clearance, GPU/CPU behavior, accuracy and denied-egress evidence
remain separate unrun gates. A Windows Job owns lifetime, not network denial.

The embedded manifest adds acquisition metadata but preserves every artifact
pin and the disabled status. Evidence must bind its new exact document hash.
Legacy v1 manifest parsing retains offline-only defaults and original-byte
hashes; old receipts are not silently rebound to the new document.
