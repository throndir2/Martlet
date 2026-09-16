# H02b: Ollama native chat wire adapter

**Internal library, fixture-backed contract only. No enabled application route.**
`Martlet.Providers.Ollama` implements one real HTTP request encoder and bounded
NDJSON response path for Ollama native `POST /api/chat`. It is not an
OpenAI-compatible `/v1` client, model manager, gateway, CLI command, or universal
Ollama adapter. Existing OpenAI behavior, Core contracts, project references,
dependency locks and HostArtifacts eligibility are unchanged.

No production authorization source is provided. Production integration remains
blocked on a reviewed host authority, trusted runtime and model selection,
permission/budget ownership, deployment and rights qualification. Constructing
the adapter, describing a selection or creating a stream performs no HTTP.
There is no default endpoint/model, credential source, discovery, retry, pull,
load/unload command, cloud fallback, registration, or startup effect.

Evidence is **controlled in-process HTTP/stream fixtures**, not Ollama inference.
No real server, runtime, model, port, certificate, credential, GPU, microphone,
speaker or Ubuntu host was accessed to validate this adapter.

## Source contract and limits of the local selector

Source was inspected on 2026-09-15 at Ollama v0.34.0 commit
`d8ab4b4f0ca24b51d3a46b3bf4f462e58ce66b1f`. The same source candidate is recorded
in the metadata-only [H02a catalog](../../deploy/ubuntu/artifacts/host-artifacts.v1.json).
Neither that metadata nor its expected archive hash is executed-runtime evidence
or permission. No upstream implementation/model assets are copied here.

| Frozen primary source | Source-observed fact |
| --- | --- |
| [Native chat reference](https://github.com/ollama/ollama/blob/d8ab4b4f0ca24b51d3a46b3bf4f462e58ce66b1f/docs/api.md#L485-L583) | `/api/chat` messages and streamed JSON; optional tools, thinking, images, format and keep-alive. Older example terminal lacks done_reason and is not accepted as proven completion by this stricter adapter. |
| [ChatRequest and Message](https://github.com/ollama/ollama/blob/d8ab4b4f0ca24b51d3a46b3bf4f462e58ce66b1f/api/types.go#L133-L210) | Distinct content/thinking/tool/image fields and explicit truncate/shift controls. |
| [Response, metrics and options](https://github.com/ollama/ollama/blob/d8ab4b4f0ca24b51d3a46b3bf4f462e58ce66b1f/api/types.go#L520-L600) | Native counts/durations, remote model/host markers, num_predict/num_ctx; no invocation digest, runtime version, request correlation or event sequence in the chat response. |
| [Source selector](https://github.com/ollama/ollama/blob/d8ab4b4f0ca24b51d3a46b3bf4f462e58ce66b1f/internal/modelref/modelref.go#L28-L118) and [server resolver](https://github.com/ollama/ollama/blob/d8ab4b4f0ca24b51d3a46b3bf4f462e58ce66b1f/server/model_resolver.go) | One `:local`/`:cloud` or legacy `-cloud` selector; nested/conflicting selectors fail; base name then validated. |
| [Name representation/parser](https://github.com/ollama/ollama/blob/d8ab4b4f0ca24b51d3a46b3bf4f462e58ce66b1f/types/model/name.go#L93-L174) and [validation](https://github.com/ollama/ollama/blob/d8ab4b4f0ca24b51d3a46b3bf4f462e58ce66b1f/types/model/name.go#L257-L283) | Actual name fields are host/namespace/model/tag. Digest-related comments do not establish an implemented digest-bound generation API. |
| [Local name lookup](https://github.com/ollama/ollama/blob/d8ab4b4f0ca24b51d3a46b3bf4f462e58ce66b1f/server/routes.go#L1223-L1250), [model cache](https://github.com/ollama/ollama/blob/d8ab4b4f0ca24b51d3a46b3bf4f462e58ce66b1f/server/model_inference_cache.go#L38-L120), [GetModel](https://github.com/ollama/ollama/blob/d8ab4b4f0ca24b51d3a46b3bf4f462e58ce66b1f/server/images.go#L713-L800) | Local manifest casing, caching and model metadata. Missing models fail; this resolution chain does not pull or retry a cloud model. Internal manifest digests are not an invocation pin. |
| [Route registration](https://github.com/ollama/ollama/blob/d8ab4b4f0ca24b51d3a46b3bf4f462e58ce66b1f/server/routes.go#L1846-L1926) and [ChatHandler admission](https://github.com/ollama/ollama/blob/d8ab4b4f0ca24b51d3a46b3bf4f462e58ce66b1f/server/routes.go#L2454-L2674) | Explicit cloud proxies early. Explicit local rejects remote-backed aliases before their proxy branch and runner scheduling. Empty messages can load/unload; model messages/system can be prepended. |
| [Completion translation](https://github.com/ollama/ollama/blob/d8ab4b4f0ca24b51d3a46b3bf4f462e58ce66b1f/server/routes.go#L2785-L2867) and [native chat translation](https://github.com/ollama/ollama/blob/d8ab4b4f0ca24b51d3a46b3bf4f462e58ce66b1f/server/routes.go#L2982-L3072) | Both local paths echo req.Model; source request context reaches runner work and terminal reasons are translated separately from text. |
| [NDJSON writer/errors](https://github.com/ollama/ollama/blob/d8ab4b4f0ca24b51d3a46b3bf4f462e58ce66b1f/server/routes.go#L2111-L2157) and [DoneReason](https://github.com/ollama/ollama/blob/d8ab4b4f0ca24b51d3a46b3bf4f462e58ce66b1f/llm/server.go#L251-L270) | Newline-delimited records; errors can change initial status or terminate a started stream. Stop/length are distinguished; connection-closed does not become a successful stop. |
| [Runner output bound](https://github.com/ollama/ollama/blob/d8ab4b4f0ca24b51d3a46b3bf4f462e58ce66b1f/llm/llama_server.go#L106-L118), [Completion](https://github.com/ollama/ollama/blob/d8ab4b4f0ca24b51d3a46b3bf4f462e58ce66b1f/llm/llama_server.go#L1532), [Chat](https://github.com/ollama/ollama/blob/d8ab4b4f0ca24b51d3a46b3bf4f462e58ce66b1f/llm/llama_server.go#L1894) | Requested output bound, internal context-canceled HTTP and prompt caching; not a universal tokenizer/compute-stop guarantee. |
| [Optional inference request logger](https://github.com/ollama/ollama/blob/d8ab4b4f0ca24b51d3a46b3bf4f462e58ce66b1f/server/inference_request_log.go#L49-L110) | Upstream can log request bodies. No Martlet logs is not server-side non-retention. |

The exact local request form is constructed, not accepted as an arbitrary wire
string. `OllamaChatModelSelection(alias, localModelName)` accepts only
`[namespace/]model:tag`, each component 1-80 lowercase ASCII characters with an
alphanumeric initial character. Remaining model/tag characters may be `.`, `_`,
`-`; namespace excludes `.`. Tags `local`, `cloud` and suffix `-cloud`, extra
colons/slashes, registry hosts, whitespace, escapes and `@digest` are rejected.
The adapter appends exactly `:local`. Even a supplied `:local` suffix is rejected
instead of being normalized again. Explicit `latest` is allowed but is never
inserted or treated as immutable.

At the pinned source, explicit local skips early cloud dispatch, resolves local
manifests and rejects configurations with both remote host and remote model
before the remote-backed-alias proxy branch. Missing/invalid models fail; there
is no adapter fallback without the suffix. Fixture errors and remote markers
exercise Martlet's behavior; they do not execute or attest this upstream trace.

**A loopback address or `:local` does not authenticate the runtime.** Another
server could ignore the selector. Rejecting remote markers after a response
cannot undo an earlier disclosure. Response model equality is an echo check,
not evidence of weights, immutable identity or local inference. No metadata
probe/check-then-generate sequence is presented as atomic model pinning.
The exact wire message is also not the whole model-rendered prompt: model-owned
messages/system/template/defaults can apply. Those remain host/model policy debt.

## Public boundary and authority ownership

The new namespace reuses unchanged `ProviderRequestContext`, `BoundedTextInput`,
`TextGenerationLimits`, outcome/detail enums and Core content/JSON contracts.
OpenAI model/credential/consent types, SSE parser and ConversationRuntime are
not generalized or borrowed. Core's real sequence validator remains the
consumer's bounded queue and epoch authority, not a duplicate adapter engine.

| API | Contract |
| --- | --- |
| `OllamaLoopbackOrigin(canonicalOrigin)` | Exactly `http://127.0.0.1:<port>`, explicit canonical decimal port 1-65535. Raw spelling checked before Uri construction; no trailing slash, DNS, IPv6, userinfo, path, query, fragment, escapes or aliases. |
| `OllamaChatOptions(temperature)` | Required explicit finite number 0-2, no arbitrary option dictionary. |
| `OllamaChatAdapter.Create(source, clock?)` | Passive owned production HttpClient/handler; no public arbitrary handler, credential or default authority. |
| `Stream(context, origin, selection, input, options, limits, callerToken, operationToken)` | Both original tokens explicit; freezes original monotonic/UTC time and one fresh immutable action. Rejects non-null personality and any history. |
| `IOllamaChatAuthorizationSource.AuthorizeAsync(action, token)` | Trusted caller must authenticate actual permission and reserve resources before returning a permit. Null denies; the interface is not implemented human authorization. |
| `OllamaChatAuthorization(action, expiresAt, dispatchLease)` | Public constructor for that trusted source, with mandatory `IAsyncDisposable` dispatch/budget lease; no boolean approval, conversion, or production issuer. |
| `OllamaChatStream` | Single-enumeration serialized pull stream of Core events, `Capabilities`, nullable `Result`, `OwnershipRelease`, captured `RequestCancellation()` and async disposal. |
| `Describe(selection)` | NotRun capabilities, not eligibility. Stream Fixture/Live provenance describes the attempt boundary, not a passed inference probe. |
| Adapter `DisposeAsync`, `IsQuarantined` | Close admission and retain actual client/handler ownership through stream/callback/lease cleanup; quarantine is a cleanup hold, never model readiness. |

Only the adapter constructs `OllamaChatAction`. It holds exact origin/endpoint,
protocol, final model string/alias, input, options, every limit, original
correlation/epoch/context deadline, fixed effective deadline and fresh action
identity. Permits match that **same action instance**, not equal hashes or a
reused request ID. Immutable records/strings cannot be changed by creating a
caller-side `with` copy. Content and lease objects are excluded from ordinary
action/permit/result JSON and `ToString`.

Every non-null returned permit is atomically consumed before binding validation.
Mismatched, expired, canceled-late and overlong permits are burned; their newly
acquired leases are cleaned up. A reused permit does not transfer or dispose
the first consumer's lease. Expiry cannot exceed the fixed original deadline.
The source cannot restart time by waiting, returning a new expiry or constructing
equal input. Failed attempts do not authorize renewal/retry.

**No production source implementation or grant factory is shipped.** Test-only
source/lease implementations issue real permits to the real HTTP path through
an internal friend-assembly handler factory fixed to Fixture provenance.
Malicious code in the same process is outside this trust boundary; the public
permit constructor does not authenticate human consent.

A future reviewed host source must supply fresh authenticated action ownership,
trusted runtime/host/model policy, actual permission, budget and dispatch lease,
revocation/original cancellation and independently qualified deployment/rights.
An OpenAI permit, H03 status credential, HostArtifacts report/hash/role,
profile, model name or callback returning true cannot be converted into a permit.
HostArtifacts candidates remain ineligible and are not consulted by this adapter.

## Exact request and transport

One HTTP/1.1-exact POST to the internally constructed `/api/chat`, accepting
`application/x-ndjson`; content is UTF-8 `application/json`. It sends precisely:

```text
model: selected base name/tag plus exactly ":local"
messages: [{ role: "user", content: exact current user text }]
stream: true
think: false
tools: []
truncate: false
shift: false
logprobs: false
options: { temperature: selected number,
           num_predict: MaxOutputTokens, num_ctx: MaxContextTokens }
```

`format` is absent: native Ollama has no documented `format:"text"` value.
No JSON/schema output is requested. `keep_alive` is absent: there is no adapter
load/unload management. The server's ordinary residency/cache defaults are not
disabled or certified. The single current message is nonempty, excluding the
empty-message load/unload API. No images, tools, context, history, remote prompt,
system/template override, debug command, logprobs payload or model discovery.

The loopback-only handler disables redirects (including same-origin), proxy,
cookies, credentials/default proxy credentials and decompression. Response
headers are bounded to 16 KiB; connection lifetime is five minutes. No TLS/key/
machine-security settings are changed. Per-request original deadlines own time;
HttpClient's independent timeout is disabled. Shared `SingleSendContent` checks
actual serializer entry and refuses repeat upload serialization. Retry-After is
only advice (0-300 seconds); no retry, fallback or model-management call follows.

## Supported NDJSON and outcomes

HTTP 200 must have application/x-ndjson with absent or UTF-8 charset and no
unrecognized parameters or encoding. Every other HTTP status fails; all 3xx are
rejected without following Location. Optional non-success bodies are not read
or mined for message/URL/keyword classification. A 404 does not prove whether
the route or model is missing. Response/content ownership is still retired.

Each normal record allows only:

| Field | Validation |
| --- | --- |
| `model` | Required exact final selected model string, including `:local`. |
| `created_at` | Required bounded RFC3339 timestamp, calendar/offset-valid, up to nine fractional digits. Not a local progress clock or required monotonic server time. |
| `message` | Exactly one object with required `role:"assistant"` and string `content`. |
| `done` | Required boolean. |
| `done_reason` | Absent/empty on nonterminal. Terminal stop/length recognized; missing/empty/unknown is incomplete. Load/unload unsupported. |
| `prompt_eval_count`, `prompt_eval_cached_count`, `eval_count` | Optional exact nonnegative decimal integers <=1 billion; cached <= prompt when both present. |
| `total_duration`, `load_duration`, `prompt_eval_duration`, `eval_duration` | Optional integer nanoseconds 0-3,600,000,000,000. |

Reject unknown fields and unsupported semantic fields even if empty/null:
thinking, tool calls/names/IDs, images, remote host/model, context, refusal,
logprobs, debug, alternate messages/choices and protocol-version wrappers.
The entire record is validated before exposing its text. The only alternate
record is exactly `{"error":"bounded message"}`; it yields an authored failure
without echoing or interpreting that string. Mixed error/generation fields fail.

Core JSON validates depth, UTF-8/surrogates and decoded duplicate keys, including
ignored/unsupported nested strings. The native framer handles arbitrary UTF-8
splits and LF/CRLF; genuine empty lines are bounded and ignored, never progress.
BOM, bare CR, whitespace-only lines, comments, SSE, sentinels and a missing final
newline are unsupported. This is a deliberately strict source-version subset.
No API version field is invented and no `/version` request is made.

Normalized Core sequence is contiguous: Started at 0, only emitted text deltas,
then one terminal for a fully consumed stream. Ollama supplies no wire sequence:
identical consecutive delta text is not deduplicated. A nonempty stop plus
verified physical EOF completes; length yields OutputTokenLimit/Core Failed.
Missing/unknown reason, absent done, malformed/truncated records, extra records/
second terminal after done or inconsistent Content-Length cannot complete.
The EOF requirement applies even without Content-Length and uses the same
original budget. Supported terminal text is held until EOF and emitted once
before its text-free terminal. Error outcomes can abort immediately.

Already emitted content remains partial if later validation/cancellation fails.
`EmittedTextCharacters` measures adapter events, not displayed/accepted/audible
content. Consumers retain original IDs/epoch and feed their actual Core
validator; the adapter never stamps late output as a new current turn.
No explicit refusal channel is defined here: natural-language refusal remains
ordinary content, never an inferred Refused event. Nor can this adapter detect
reasoning/instructions falsely labeled by a runtime as ordinary content.

Usage contains only validated terminal native counts and four durations;
intermediate numbers are validated, not summed. Missing fields stay null.
Reported prompt/eval counts above authorized input/output limits fail, but this
does not prove work was prevented upstream. No total-token sum, measured cost,
throughput, model digest, quality, memory fit or actual compute cancellation.

## Bounds, cancellation and retained ownership

Existing TextGenerationLimits provide input bytes and local byte-plus-reserve
admission (not a tokenizer); output/context ceilings 4,096/32,768; line <=256 KiB,
body <=4 MiB, <=4,094 JSON records and <=16,384 UTF-16 text units. Encoded request
JSON is additionally <=128 KiB. The reader owns one bounded line and 4 KiB
read-ahead, never a producer queue or whole-response buffer. Overrun detection
reads at most one byte beyond the configured total. Core owns its own bounded
consumer queue, backpressure, Stop and epoch transitions.

First-text/idle/total default to 15/10/60 seconds (each <=120). Clocks start at
Stream creation, including scheduling/authority delay and consumer pauses.
Only nonempty accepted text renews idle progress. Absolute UTC AND original
monotonic duration constrain request and permit; rollback/forward clock jumps
and delayed timers cannot extend permission. Original caller, operation and
enumerator flags are checked directly, not only their linked descendants,
across authority, serialization, header/stream/read awaits and publication.
A blocked newer LIFO callback cannot conceal those flags.
All original-source and deadline/progress signals feed one owned cancellation
token through its observed CancelAsync task. Retirement joins that task and
detaches the signal registrations; callbacks already running from an original
caller or timer cannot escape the cleanup-failure/quarantine boundary.

Each stream has its own cancellation handle and resources. Concurrent pulls
are rejected. `DisposeAsync` requests cancellation and awaits an actual pending
pull/authority/read; it does not abandon work on timeout. A normal consumer
uses `await foreach` or disposes its enumerator, and awaits **successful**
OwnershipRelease. Early disposal creates a canceled result without pretending
to have delivered a terminal event.

The adapter owns its handler until actual stream work, cancellation callbacks,
request/response/body and lease cleanup finish. Noncooperative work keeps
ownership pending. A cleanup exception produces sanitized
`OllamaChatCleanupException`, faulted release and quarantine; it does not
release a still-unproven dispatch lease or allow precreated/new streams to
dispatch. No automatic cleanup retry or replacement factory. Unexpected
programming exceptions surface after owned cleanup rather than becoming success.
Do not log raw external exceptions, HTTP messages or content-bearing events.

No ordinary logs are emitted. Managed strings/HTTP buffers are not claimed
securely erased. Local request abort does not prove backend computation stopped,
models unloaded, content deleted, no retention or avoided costs. Outer host
authority owns aggregate concurrency; there is no pending-turn queue here.

## Local reproduction and retained gates

Use the pinned SDK and the [local-only policy](../../README.md#local-only-validation-policy).
Keep output/temp/CLI-home/NuGet paths in an explicitly chosen private C: directory
on the pooled-drive developer host; never use D: for VSTest outputs. These are
developer commands, not a customer invocation or model smoke:

```powershell
$env:CI = 'true'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
# Set $sdk and private $artifacts, DOTNET_CLI_HOME, NUGET_PACKAGES, TEMP/TMP first.
& $sdk restore tests\Martlet.Providers.Tests --locked-mode --artifacts-path $artifacts
& $sdk build tests\Martlet.Providers.Tests -c Release --no-restore --artifacts-path $artifacts
& $sdk test tests\Martlet.Providers.Tests -c Release --no-build --no-restore --artifacts-path $artifacts --results-directory "$artifacts\TestResults" --filter 'FullyQualifiedName~Tests.Ollama'
```

In the approval-constrained implementation run, actual missing-assets failures
were retained before locked restores. No locks were regenerated, packages added,
SDK installed or shared SDK changed. Providers/Core baseline and affected
Providers/Core/Conversation/Participation regressions are separate from new
fixture acceptance. Handler/stream fixtures exercise the production encoder,
authority/lease ownership, HTTP dispatch, parser and actual Core validator.
They cover malformed/bounded protocol, exact input/source/origin binding,
cancel/deadline/blocked-callback paths, noncooperative cleanup/quarantine,
concurrency, EOF, errors and privacy. No listener or real Ollama test is provided.

**Still NOT RUN / NOT PASSED:** production issuer/integration, trusted runtime/
host/locality, weights/digest binding, model quality and token enforcement,
native cancellation, rights, payload/image/dependency closure, Ubuntu/GPU fit,
LLM/F5 combined operation, real network/OS/device and H01/H03/H04/H05/H06/G3
qualification. H02 is not complete and no shipped companion route is claimed.
