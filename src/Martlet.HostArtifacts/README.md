# Offline host artifact inspection (H02a)

**Standalone internal managed-code foundation, not a shipped installer feature.**
The library and `Martlet.ArtifactDoctor` inspect one explicitly selected local
JSON document. They do not install, download, verify model payloads, execute
commands, resolve URLs, inspect caches, probe the host/GPU, load models, modify
settings, approve rights, or enable a preset. All candidates remain disabled.

These three projects intentionally remain outside `Martlet.slnx` and the
Desktop/existing Doctor/Windows package graph. Core's parser is reused without
changing Core, its enums, settings or dependency authority. The library has no
external package dependencies. The dedicated suite uses the repository's
existing central Test SDK/xUnit pins.

## Direct local workflow

Use the exact SDK in `global.json`. These are developer commands, not end-user
installation instructions. On the current Windows developer environment, keep
all build/test/temp output on an explicitly selected private **C:** directory
because native VSTest is unstable on the pooled D: source drive. Choose your own
existing local SDK; no bootstrap or SDK installation is performed by this tool.

From the repository root, with `$sdk` set to the local `dotnet.exe`:

```powershell
$artifacts = 'C:\YOUR-PRIVATE-DEVELOPMENT-DIRECTORY\ci-artifacts'
$env:CI = 'true'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
& $sdk restore tests\Martlet.HostArtifacts.Tests\Martlet.HostArtifacts.Tests.csproj --locked-mode --artifacts-path $artifacts
& $sdk build tests\Martlet.HostArtifacts.Tests\Martlet.HostArtifacts.Tests.csproj -c Release --no-restore --artifacts-path $artifacts
& $sdk test tests\Martlet.HostArtifacts.Tests\Martlet.HostArtifacts.Tests.csproj -c Release --no-build --no-restore --artifacts-path $artifacts --results-directory "$artifacts\TestResults"
$doctor = "$artifacts\bin\Martlet.ArtifactDoctor\release\Martlet.ArtifactDoctor.dll"
$manifest = "$PWD\deploy\ubuntu\artifacts\host-artifacts.v1.json"
& $sdk $doctor inspect --manifest $manifest --json
& $sdk $doctor inspect --manifest $manifest --role f5-tts --target ubuntu-24.04-x64
```

Also point `DOTNET_CLI_HOME`, NuGet caches and `TEMP`/`TMP` at your explicit
private development directory where isolation is required. Initial NuGet
restore can contact NuGet for the pinned development packages; **inspection
and tests never fetch the URLs in a manifest**. No remote CI is provided.
Normal validation is locked. The initial authoring step generated only the
three new project locks with `--use-lock-file -p:RestoreLockedMode=false`,
reviewed unchanged existing locks/pins, then performed locked restores. That
one-time generation is not an ordinary validation command or persistent
project override.

The CLI also supports `--help`, `-h` and `--version`. Inspection requires
`inspect --manifest ABSOLUTE_LOCAL_JSON_PATH`, optional `--role ROLE_ID`,
optional `--target TARGET_ID`, and optional `--json`, each at most once.
Omitted role selects all roles in this document; omitted target remains
unknown. A selector such as `ubuntu-24.04-x64` is supplied intent, not evidence
of the machine running the tool.

| Exit | Meaning |
| --- | --- |
| 0 | Help/version only |
| 1 | A valid inspected candidate's declared target differs from the requested target |
| 2 | Valid but disabled/incomplete candidate, or explicitly reported cancellation/read deadline |
| 3 | Invalid invocation, unsupported/invalid/inconsistent/oversized document, inaccessible/disallowed input |

Exit 2 is expected for the committed catalog, including a matching declared
Ubuntu target. No `ready` field or successful installation outcome is emitted.
Inspection always computes `execution_eligible`, `download_authorized`,
`host_qualified`, `payload_verified` and `source_assertions_authenticated` as
false. GPU fit is `not_measured`, rights are `not_reviewed`, prerequisite
observations are `not_performed`, and runtime closure is `not_established`.

## Production API and report

`ArtifactManifestReader.Read(ReadOnlyMemory<byte>)` copies bounded input and
returns an owned `ArtifactManifest`. Its wire models are internal; callers
cannot mutate validated arrays. `ArtifactInspector.Inspect(manifest, roleId,
target)` computes the actual direct/transitive closure and returns an immutable
`InspectionReport`. `ToJson()` and `ToHuman()` format the same report;
`ExitCode`, `KnownPayloadBytes` and `DocumentSha256` are directly available.
Invalid input raises a sanitized `ArtifactManifestException` containing a
stable diagnostic code/remedy, never a supplied path or raw parser exception.

The report binds the exact input-byte SHA-256, format, role selection and
requested target. It includes sorted sources/runtimes/artifacts/license
claims, inert dependency declarations, required missing component kinds and a
prerequisite inventory. OS/kernel, CPU features, RAM/swap, storage, GPU
identity/VRAM/driver, container tooling, framework/CUDA compatibility and
combined fit remain **not observed**. F5 additionally requires reference-voice
rights and supplied reference text.

This is reproducible metadata, not a qualification receipt or approved
provisioning plan. Identical bytes and arguments produce identical facts and
JSON; no current timestamp, random identifier or machine path is added.
Different whitespace changes the exact document fingerprint. No source
signature or API assertion is authenticated by offline parsing.

## Version 1 contract

The [candidate document](../../deploy/ubuntu/artifacts/host-artifacts.v1.json)
is the concrete example and pin authority. All properties on the following
wire objects are required, including explicitly nullable properties. JSON is
snake_case with exact named enum tokens; unknown fields are rejected at every
level. The version is inspected first. Comments, trailing commas/content,
duplicate decoded properties, malformed UTF-8/surrogates, numeric/composite/
case-aliased enum values and unsupported versions fail closed.

| Object | Fields |
| --- | --- |
| Document | `format_version` (1), `kind` (`host_artifact_candidate_lock`), `id`, `provenance` (`upstream_metadata`/`synthetic_fixture`), `scope` (`metadata_only`), `sources`, `runtimes`, `artifacts`, `licenses`, `roles` |
| Source | `id`, `kind` (`github`/`hugging_face`), `repository`, full `revision`, `commit_url` |
| Runtime | `id`, `family` (`ollama`/`f5_tts`), `role` (`llm`/`tts`), `source_id`, `upstream_version`, `target`, `license_id`, `dependency_evidence_path`, `dependency_evidence_url`, `dependencies`, `unresolved` |
| Dependency | Canonical hyphenated `name`, `ecosystem` (`python`/`python_build`/`native`), `condition` (`always`/`python_at_most310`/`not_darwin_and_not_arm64`), `constraints` |
| Constraint | `comparison` (`at_least`/`greater_than`/`at_most`/`exact`), numeric dotted `version`; contradictory/duplicate bounds rejected, not a general Python version resolver |
| Artifact | `id`, `kind`, `source_id`, upstream `path`, positive exact `bytes`, nullable `sha256`, `sha256_evidence`, nullable `git_blob_sha1`, `source_url`, `evidence_url`, nullable `release`, `license_ids`, `depends_on` |
| Release | Numeric `release_id` and `asset_id`, descriptive `tag`, `metadata_url`; expected content hash remains mandatory |
| License | `id`, `source_id`, `scope` (`source_code`/`model_repository`/`runtime_archive_components`), `spdx` (`MIT`/`CC-BY-NC-4.0`/`unknown`), `disposition` (`unreviewed` only), nullable `evidence_path`/`evidence_url` |
| Role | `id`, `role`, `runtime_id`, `target`, `root_artifact_ids`, `components` |
| Component slot | Typed artifact `kind`, nullable `artifact_id`; null means explicitly missing, not zero bytes |

Artifact kinds are `runtime_archive`, `llm_weights`, `tts_weights`,
`vocabulary`, `vocoder_weights`, and `vocoder_configuration`. Ollama requires
runtime/archive and LLM-weight slots; F5 requires runtime/archive, TTS weights,
vocabulary and both vocoder slots. The concrete runtime families reference the
named Ollama/F5 GitHub source repositories, not arbitrary providers.

Slots must be present exactly once and match bound artifact kinds. A role's
root closure must equal its bound components. F5 weights require edges to
present vocabulary/vocoder weights; vocoder weights require the configuration.
Present Ollama LLM weights require their runtime archive. Missing components
are reported even if a caller empties `unresolved`. Mandatory image, runtime
closure, rights, host, disk and fit gaps are derived by the inspector, never
trusted from an input warning list. Labels cannot bypass the typed family.

`unresolved` records are supplemental declarations (`runtime_image`,
`runtime_dependencies`, `archive_components`, `expanded_bytes`). Dependency
constraints remain upstream statements, not resolved package versions or
commands. The F5 inventory preserves all 28 base dependencies and 2 build
requirements at the selected source; optional evaluation extras are excluded.
The three Ollama native entries are observations from its source build recipe,
**not** a complete inventory of the release archive or its licenses.

Source/runtime/license references, uniqueness, missing edges, self/cycles,
unreachable records, case/path/content aliases, required family relationships
and checked byte sums are validated before role filtering. Shared artifacts
use one ID, even when reached along multiple graph paths. Source-code MIT
evidence cannot license a separate model or an archive's bundled components.
GitHub repository/asset identity is unique across the entire document,
independent of filename, hash, byte count, source revision or release metadata.
Two contradictory observations cannot become separate payloads through
runtime/role aliases; different assets within one release remain distinct.
For HF records declaring `hugging_face_lfs_metadata`, a repeated Git blob
identity must agree on its declared payload SHA-256 and payload byte count
across the whole document, regardless of repository, revision or path.
Payload bytes are never compared with a pointer blob's own size. Unavailable
hash evidence supplies no LFS payload association. Consistent references may
share one artifact; existing duplicate-payload alias rules still apply.
This consistency check grants no payload verification, checksum promotion,
publisher trust, rights or execution permission.

### Bounds and inert locators

| Resource | Bound |
| --- | --- |
| Input and output | 256 KiB each; depth 16; input read stops at limit + 1 |
| Collections | 8 sources, 4 runtimes, 4 roles, 64 artifacts, 16 licenses |
| Dependencies | 128 declarations/supplemental unresolved records total; 256 artifact edges, 16 edges per node; 4 comparisons per package |
| IDs | 1-64 lowercase ASCII letters/digits/dot/underscore/hyphen, initial alphanumeric; dependency names use canonical hyphenated alphanumeric segments |
| Repository segments/version | At most 128 ASCII characters; revisions exactly 40 nonzero lowercase hex |
| Upstream paths/URLs | 512 path characters, 128 per segment; URL 2,048 characters |
| Payload bytes | Positive integer, at most 16 TiB per artifact; total at most 64 TiB |
| Findings/prerequisites | At most 128 findings and 16 prerequisite records; authored summaries/remedies at most 512 characters |
| CLI | At most 8 arguments, 4,096 characters each; explicit local `.json` input, 10-second bounded asynchronous read |

Only exact GitHub source/release-asset and Hugging Face commit-file/evidence URL
shapes are accepted. Equality is checked against raw canonical text, before URI
normalization could erase traversal. HTTPS origin, repository, revision,
relative path and release/asset IDs must agree. No userinfo, query/token,
fragment, alternative port/scheme, localhost/private/IP origin, encoded alias,
redirect URL, or hostname suffix trick is accepted. URLs are **never fetched**.

Relative upstream paths are data, not extraction destinations or local reads.
They reject traversal/empty segments, roots/drives, backslashes, encoding,
controls, device names and trailing dots/spaces. The local CLI file boundary
rejects UNC/device/alternate-stream paths and link/reparse ancestors; it does
not enumerate directories or read any referenced artifact. Expected IO/parser
failures are sanitized. Hostile concurrent filesystem substitution, special
mounts and native Linux execution are not qualified by these Windows-local
checks; this utility is not a privileged file authority or bootstrap engine.

## Candidate facts and evidence limits

Official metadata was read on 2026-09-15. No model weights, vocabulary payload,
runtime archive/image, repository clone, or executable was acquired or run.
The source and API locators, full SHA-256/commit/blob identities and license
evidence are recorded in the JSON document.

| Listed subset | Declared bytes | Remaining gap |
| --- | --- | --- |
| Ollama `v0.34.0` Linux amd64 archive, asset `553800779` | 1,433,537,033 | No local payload/build verification, image digest, LLM weights, complete bundled dependencies/licenses or expanded size |
| F5 v1 weights + vocabulary | 1,348,449,561 | Vocabulary has a Git blob ID but no published plain SHA-256 in inspected metadata; CC-BY-NC-4.0 rights remain unreviewed |
| Vocos weights + configuration | 54,366,452 | Configuration has a Git blob ID but no published plain SHA-256 in inspected metadata; MIT model-card declaration is not use/voice approval |
| F5/Vocos subset | 1,402,816,013 | F5 runtime/image/transitive/native dependency closure still missing |
| Combined five-file subset | 2,836,353,046 | **Not** complete download or installed/peak/free disk requirements |

Official source references:

- [Ollama release asset API](https://api.github.com/repos/ollama/ollama/releases/assets/553800779);
  source [commit d8ab4b4f0ca24b51d3a46b3bf4f462e58ce66b1f](https://github.com/ollama/ollama/commit/d8ab4b4f0ca24b51d3a46b3bf4f462e58ce66b1f).
  Release `383028723` reported `immutable: false`; the version label alone is
  not a pin or attestation. The source recipe's CUDA stages/base tag are not
  proof of shipped binaries or a numeric driver requirement.
- [F5 source dependency metadata](https://raw.githubusercontent.com/SWivid/F5-TTS/9c614e9657089213efc6a7421b30630be138a3f5/pyproject.toml),
  [MIT source license](https://raw.githubusercontent.com/SWivid/F5-TTS/9c614e9657089213efc6a7421b30630be138a3f5/LICENSE),
  and [auxiliary-loading source](https://raw.githubusercontent.com/SWivid/F5-TTS/9c614e9657089213efc6a7421b30630be138a3f5/src/f5_tts/infer/utils_infer.py).
  This source declares 1.1.22, uses Vocos, and can load Whisper for blank
  reference text. H04 must control that path explicitly.
- [F5 model tree](https://huggingface.co/api/models/SWivid/F5-TTS/tree/84e5a410d9cead4de2f847e7c9369a6440bdfaca/F5TTS_v1_Base)
  and [CC-BY-NC-4.0 model card](https://huggingface.co/SWivid/F5-TTS/raw/84e5a410d9cead4de2f847e7c9369a6440bdfaca/README.md).
- [Vocos tree](https://huggingface.co/api/models/charactr/vocos-mel-24khz/tree/0feb3fdd929bcd6649e0e7c5a688cf7dd012ef21)
  and [MIT model card](https://huggingface.co/charactr/vocos-mel-24khz/raw/0feb3fdd929bcd6649e0e7c5a688cf7dd012ef21/README.md).

Keep these distinct: **Git commit identity**, **Git blob SHA-1 identity**,
**API/publisher-declared payload SHA-256 and byte count**, **locally computed
input-document hash**, and **verification of acquired payload bytes** (not
performed here). An LFS pointer blob is not its weights. A normal Git blob
includes Git framing and is not a plain-file SHA-256 or modern download proof.
`upstream_metadata` in a user-supplied document is a claim, not authentication.
All license cards/files are declarations, not Martlet/project/voice rights grants.

## Coverage and retained gates

The dedicated suite exercises the actual parser/graph/inspector, catalog,
human/JSON command path and executable subprocess; strict shape/null/enum/
Unicode/limit cases; source/path/pin attacks; graph/role/license inconsistencies;
invariant totals/fingerprints; source-evidence distinctions; mandatory gaps;
and private-file preservation/cancellation/IO errors. Core and existing Doctor
regressions run separately. Local fixtures never stand in for model execution.

H02 still owns the LLM backend spike/selection, weights, immutable runtime
images and complete native/transitive dependency/license inventories. H01
owns actual host observations; H04 owns F5 behavior/reference consent; H05 owns
reviewed setup/download consent, redirect policy, actual content verification,
disk reservations and lifecycle; H06 owns exact native/GPU/combined-role fit.
No H01/H03 unmerged code was copied or referenced. Linux, real GPU/runtime,
rights, installation and G3 qualification remain **NOT RUN / NOT PASSED**.
