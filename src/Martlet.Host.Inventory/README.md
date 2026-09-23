# Portable host report metadata

`Martlet.Host.Inventory` owns validated H01 v1 report contracts, their in-memory
JSON codec, fixed candidate/remedy metadata and human-readable **inspection**.
It is a net10.0 library with no product project/package dependencies or entry point.
It does not collect inventory, evaluate raw host output or execute remedies.

## Trust and qualification boundary

**Every input report is unauthenticated metadata.** `LiveLocal`, `Observed`,
timestamps, source descriptions and the legacy `ScopeNotice` are report claims,
not evidence that this library collected anything. A valid document can be
authored by anyone. Decoding, validation, a fingerprint or exit 0 establishes
neither authenticity nor execution permission. `ReportFormatter.Human` always
labels its output unauthenticated, including when the wire tag is `LiveLocal`;
legacy notices are displayed as reported claims.

`DeploymentQualified` is always false. No qualification/admission API is provided.
The candidate remains `CandidateNotQualified`, exact versions and its entire
qualified tuple remain unknown, and Compose major 2 is only a family constraint.
Docker daemon access is unknown; container GPU, inference, clock accuracy and
pairing/firewall checks remain explicitly not run. Unknown/disabled prerequisites
never default to success. Inventory exit 0 means only that required *reported*
inventory rows are observed, not that a host, GPU or model is ready.

**H01 PR #21's exact-native Linux trace/reproducibility and publication hold is
unchanged.** Managed serialization tests, historical serializer fixtures and this
metadata-only source extraction are not Linux execution, hardware, deployment,
setup or signed-release evidence. No held native implementation is included.

## Supported use

```csharp
using Martlet.Host.Inventory;

HostReport metadata = HostJson.Deserialize(documentBytes);
string canonicalJson = HostJson.Serialize(metadata);
string unverifiedInspection = ReportFormatter.Human(metadata);
```

Callers supply already bounded bytes; there are no path, file, stream, process,
clock-observation, OS, network, Docker or daemon APIs. Deserialization owns a copy
of at most 262,144 bytes and limits JSON depth to 16. Both read and write accept an
optional smaller byte ceiling (1 through 262,144). Domain validation bounds the
record count and every free-form evidence value before serialization. Invalid
metadata fails with sanitized `InvalidDataException`; invalid caller size limits
throw `ArgumentOutOfRangeException`, and a null serialization argument throws
`ArgumentNullException`. There are no fallback reports.

The supported reader requires the **complete v1 serializer shape**, including
explicit null fields and computed fields. It rejects duplicate decoded property
names, invalid UTF-8/escaped surrogates, unknown/missing members, enum numbers or
alternate spellings, inconsistent records and tampered computed metadata.
Property order and insignificant JSON whitespace may vary. After typed validation,
semantic comparison against canonical serialization checks fields that
System.Text.Json would otherwise ignore, including `deploymentQualified`,
`candidateRequirements`, `scopeNotice`, `exitCode`, state, summary and remedies.
It does not normalize foreign date/string representations into a different wire
document. `HostJson.Options` remains the historical low-level serializer metadata
surface; direct `JsonSerializer.Deserialize` is **not** the validated reader.

Canonical serialization explicitly uses **CRLF on every platform**. This is a
new deterministic whitespace policy, preserving the captured Windows historical
serializer bytes without changing v1 JSON meanings. The old serializer's newline
was platform-dependent: there is no claim of byte parity with every historical
platform or of tested Linux native output. LF documents and other insignificant
whitespace are accepted and normalize to canonical CRLF. The four original
Windows-produced golden documents have fixture-local `-text` Git attributes so
their captured bytes and hashes survive checkout unchanged.

`HostReport.Validate` and `EvidenceRules` are the single domain-validation owner
for the codec and formatter. This preserves legitimate historical producer output
while rejecting previously unchecked null/default values, inconsistent age
claims and attempts to populate unsupported future qualification rows.
`CandidateManifest.Validate` checks the fixed v1 candidate boundary; it does not
add an artifact-selection or qualification engine.

## Source and migration

Narrow extraction from `3d9707cda7bc5a179d31cd7db5aa3f8756d7bb97`, locally merged
by `22d76dfdd278d24b8a2d9c7b87621216dc93c687`:

| Historical source | Shared owner |
| --- | --- |
| `src/Martlet.Host.Doctor/Contracts.cs` | `Contracts.cs`, `HostJson.cs` |
| `EvidenceRules.cs`, `CandidateManifest.cs`, `RemedyCatalog.cs`, `ReportFormatter.cs` | Corresponding metadata files |
| `Parsers.cs` | Only `SafeVersion`, `SafeOsVersion`, `SafeGpu` and their regexes in `EvidenceTextRules.cs` |
| `HostSources.cs` | Only fixed `Description` strings in `EvidenceSources.cs` |
| `deploy/ubuntu/preflight/candidate-requirements.json` | Local embedded candidate resource |

New work adds the bounded memory reader, explicit inspection trust warning,
fail-closed validation hardening and pure regression tests. The original H01
has no report reader. Existing Core and Updates codecs cannot be directly reused:
Core hardcodes snake_case/provider contracts; Updates' reader is private and
update-specific. The small JSON structural guard follows those existing patterns
without duplicating host-domain validators or changing their owners.

No evaluator, snapshot collector, full native parser, CLI, process runner, interop,
production fixture engine, native package, deployment script or workflow was
ported. The historical branches and worktrees are untouched.

Subsequent Host.Setup and qualified Host.Doctor ports must reference this project,
change `using Martlet.Host.Doctor` to `using Martlet.Host.Inventory` for metadata,
and **remove their original contract/validation/codec/formatter copies** in the
same integration. Collector code must use `EvidenceSources.Description` for the
canonical strings; evidence validation belongs here rather than in a new parallel
contract. The unchanged v1 camelCase names, enum names, candidate ID and computed
wire values are covered by historical serializer fixtures. This library is the
only current-main contract owner, not an independently evolving Doctor variant.

The old H05a `SetupPlanBuilder`'s `LiveLocal` comparison must not become an
authentication or setup authorization boundary when ported. A later executable
integration requires separately reviewed observation identity, freshness and
authorization guarantees. This PR does not provide or simulate those guarantees.

## Local checks

The standalone test project does not require a solution change:

```powershell
dotnet restore tests\Martlet.Host.Inventory.Tests\Martlet.Host.Inventory.Tests.csproj --locked-mode
dotnet test tests\Martlet.Host.Inventory.Tests\Martlet.Host.Inventory.Tests.csproj -c Release --no-restore
```

Use the repository-pinned SDK and local artifacts directory. Tests exercise real
production validation/serialization/inspection on authored records, not a mock
codec, live host, inference workload or qualification probe. See the test
fixtures' README for exact historical producer provenance and hashes.
