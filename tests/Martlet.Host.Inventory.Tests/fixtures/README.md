# Authored records, historical serializer output

These four complete JSON documents were produced by the **original**
`HostJson.Serialize` at `3d9707cda7bc5a179d31cd7db5aa3f8756d7bb97` (local merge
`22d76dfdd278d24b8a2d9c7b87621216dc93c687`), using SDK 10.0.401 in Release mode
on Windows. They are not hand-written expected JSON or new-codec output.

Inputs are the authored records in `..\ReportFixtures.cs`, using the original
`Martlet.Host.Doctor` namespace and its original fixed source descriptions.
The timestamp is explicitly authored, `2026-09-13T00:00:00+00:00`, with report
creation ten seconds later. The GPU model/version, byte counts and platform values
are fixture data, never observations of the executing machine.

An isolated temporary metadata-only harness compiled the original `Contracts.cs`,
`EvidenceRules.cs`, `CandidateManifest.cs`, `RemedyCatalog.cs` and
`ReportFormatter.cs`, plus only the three pure version/GPU predicates and their
regexes from `Parsers.cs`, and only `HostSources.Description`. It embedded the
unchanged historical candidate JSON under its original resource name. For each
case it passed `ReportFixtures.Create(name)` to original `HostJson.Serialize` and
wrote the result as UTF-8 without a BOM or added newline. No executable project,
collector, HostEvaluator, native parser, CLI, process probe or packaging code was
compiled or run. The temporary legacy source is not part of the repository.

The old serializer used platform-dependent newlines; these Windows outputs
contain CRLF. Fixture-local `*.json -text` attributes retain those exact captured
bytes in Git. The new codec explicitly pins canonical CRLF on all platforms,
a documented new whitespace policy with unchanged v1 JSON meaning. LF-input
tests are synthetic transformations, not captured Linux evidence. No byte parity
with every historical platform is asserted, and Linux native output is untested.

These are **historical serializer outputs on authored inputs**, not historical
CLI/evaluator captures, live host reports or native qualification evidence. No
Windows test waives H01 PR #21's Linux trace/reproducibility/publication hold.
The compatibility test requires the new reader to accept these exact bytes and
the new serializer to reproduce them exactly; it also checks the current authored
records against the captured bytes.

| File | Bytes | Report exit | SHA-256 |
| --- | --- | --- | --- |
| `inventory.v1.json` | 29941 | 0 | `ffd614da37275aaf31b7020138a85785e31a70ee5f2613d395987d8bad15daff` |
| `prerequisites.v1.json` | 32346 | 2 | `7092253570ab0ce372b394f2df7a216d3b291b5daccb08d91f547a6aef776cbe` |
| `missing-tools.v1.json` | 32355 | 1 | `919a72765929364afe6079eb7277a54bff810fb7062d268d4eaf8fb6eb2a643e` |
| `unsupported.v1.json` | 26346 | 3 | `1b349da5fec9fa4d38613c5f5b04531be691fb7dc3c849b5e95a35932cacebde` |

Inventory has all required authored observations. Prerequisites keeps daemon
access/qualified versions unknown and future qualification rows not run.
Missing-tools marks Compose absent. Unsupported retains an authored non-Linux
platform and leaves all other rows not run. All four have `AuthoredFixture`
provenance and `deploymentQualified: false`, including the inventory exit-0 case.
