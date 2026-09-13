# Named OpenAI provider adapters: V03a and V03b

Provider library only: bounded-file transcription (V03a) and bounded text
Responses streaming (V03b). Neither adapter is wired into the application.
There is no automatic network activity, credential lookup, retry, model
selection, cloud fallback or live qualification. V03c TTS and V04 orchestration
remain separate work.

## V03a: bounded-file transcription

This is a production HTTP adapter library, **not a working voice application**.
The transcription adapter implements only `POST https://api.openai.com/v1/audio/transcriptions` with a
completed, bounded mono PCM16 WAV and one final JSON response. There is no
microphone, VAD, resampler, codec download, model download, TTS, onboarding,
Credential Manager implementation, Doctor probe, discovery request, or automatic
network activity. It references Core only, using BCL HTTP/JSON and no new package.

**Evidence:** production-path in-process HTTP fixtures, explicitly `Fixture`,
not live inference. Authorized account/model/audio smoke: **NOT RUN**. No API
keys, billable requests, free-service claims, product-ready flag, GPU evidence,
or release qualification are supplied by this slice.

## Upstream sources and intentionally narrow scope

Read-only primary sources accessed **2026-09-12 (America/Los_Angeles)**:

| Source | Observed contract and implementation consequence |
| --- | --- |
| [OpenAI speech-to-text guide](https://developers.openai.com/api/docs/guides/speech-to-text) | Completed-file transcription differs from incoming-audio realtime transcription; 25 MB upstream file limit. The guide now recommends `gpt-transcribe` and documents `languages: []` when detection is unreliable. This is upstream documentation, not an account-access observation. |
| [Create transcription API reference](https://developers.openai.com/api/reference/resources/audio/subresources/transcriptions/methods/create) | Final JSON contains required string `text`, optional detected `languages` and optional token/duration usage. The adapter does not request timestamps, logprobs, diarization or streamed events. |
| [Official OpenAPI-generated request schema at openai-python `e12b81d3bbf644ec7045e152d69bc4b68d69cd48`](https://github.com/openai/openai-python/blob/e12b81d3bbf644ec7045e152d69bc4b68d69cd48/src/openai/types/audio/transcription_create_params.py) | Required multipart `file` and explicit `model`; an extension-bearing filename and correct content type are recommended. `response_format=json`, `stream=false` are the selected subset. The schema lists the exact model IDs below. It accepts more codecs than the guide lists; this adapter accepts only their intersection, WAV, with stricter local validation. No SDK/source code is copied or installed. |
| [OpenAI error codes](https://developers.openai.com/api/docs/guides/error-codes) | HTTP 401/403, 429 rate versus credits/spend/usage limits, and 500/503 remain distinct. Only recognized machine codes refine the status; error messages never enter local results. |
| [OpenAI API pricing](https://developers.openai.com/api/docs/pricing) and [data controls](https://developers.openai.com/api/docs/guides/your-data) | Mutable prices and endpoint/account-dependent retention need review at onboarding and live qualification. No-training-by-default is not no-retention. No permanent price, quota, retention guarantee or zero-cost estimate is embedded here. |

`OpenAiTranscriptionCatalog.SupportedModelIds` is a dated adapter allowlist:
`gpt-transcribe`, `gpt-4o-transcribe`, `gpt-4o-mini-transcribe`,
`gpt-4o-mini-transcribe-2025-12-15`, and `whisper-1`.
All use the same tested bounded-file JSON subset; all remain **live unverified**.
An ID is always explicitly supplied, never selected by construction or fallback.
Aliases may drift upstream; even the dated ID is not evidence of account access.
Unknown IDs and `gpt-4o-transcribe-diarize` fail locally. Adding models requires
reviewing their concrete schemas and fixtures, not accepting arbitrary strings.

`Describe(modelAlias, upstreamModelId)` validates but performs no I/O. Its
Core `ModelId` is the caller's **internal alias**, under Core's unchanged
identifier rules. The separate upstream ID goes in multipart and consent/
credential scope; it is never derived from the alias. Catalog capabilities are
`NotRun`, STT partials unsupported, cancellation unknown. An observed attempt
may be described by its owner as request-abort capable, **not** cooperative
compute cancellation. No model-list/HTTP-200 readiness inference exists.

## Public APIs and integration

| Surface | Contract |
| --- | --- |
| `BoundedWaveAudio.FromPcm(PcmFormat, ReadOnlySpan<byte>)` | Validate and copy an entire utterance, construct a canonical WAV. Uses Core's supported PCM rates/encoding, but not its 100 ms frame limit. |
| `BoundedWaveAudio.FromWave(ReadOnlySpan<byte>)` | Validate actual RIFF/fmt/data sizes, PCM fields, alignment and duration before copying. Only canonical 44-byte-header WAV is accepted; extra metadata/chunks, compressed/extensible WAV, stereo and trailing bytes are rejected, not forwarded. |
| `ProviderRequestContext` | Existing `CorrelationIds`, original epoch and absolute deadline. Epoch must leave room for Core Stop. Immutable request context is retained on every result, including cancellation and late arrivals. |
| `TranscriptionLimits` | Caller-visible upload bytes/duration, response/text bounds and total request time. Must match the authorized values exactly. |
| `AudioUploadAuthorization` | Explicit single-use permission for audio disclosure **and potential charges**, bound to exact origin, STT role, upstream model, all correlation IDs, epoch, limits and expiry. No automatic approval factory. |
| `IProviderCredentialSource.ResolveAsync(binding, token)` | Injected OS-store integration seam. Return a freshly owned `BoundProviderCredential` for that exact origin/role/model, or null. Translate expected store failures into `CredentialUnavailableException`; honor cancellation. Unexpected programming exceptions are not silently swallowed. |
| `OpenAiTranscriptionAdapter.Create(source, timeProvider?)` | Passive, safe production factory. Owns one reusable HttpClient and its handler until adapter disposal. No arbitrary HttpClient, handler or endpoint is accepted by the public API. |
| `TranscribeAsync(context, upstreamModelId, audio, limits, authorization, token)` | Check validated context, cancellation/deadline, model and authorization before resolving a secret or constructing/serializing the upload; check returned credential binding before attaching it. One send, no retries/fallback. |
| `TranscriptionResult` | Explicit outcome, text/languages content, original context, provenance, nullable confidence, known/unknown usage, optional sanitized failure. Text/languages are omitted from ordinary JSON serialization; `ToString()` contains outcome only. |
| `ToTerminalEvent()` | Core terminal event, original IDs/epoch, provider `openai`, sequence 1. Caller supplies matching `Started` at sequence 0 before the attempt. Completed/no-speech/canceled/failed mapping goes through the unchanged Core validator. The event is an explicit **content envelope**, not log metadata. |

V01 should accumulate bounded, contiguous consented capture into an utterance
and pass mono PCM using `FromPcm`; this library does not record, resample or
decide whether a speaker authorized capture. `PcmFrame` is a different boundary:
the adapter accepts a whole utterance longer than 100 ms without relaxing Core.

V02 owns model/account configuration, actual human consent, data retention
disclosure, session budgets and OS credential storage/rotation. An authorized
calling path constructs `AudioUploadAuthorization` only after obtaining that
permission. **This library cannot authenticate human consent or defend against
malicious code in its own process.** A credential, placeholder key, profile,
saved settings, catalog entry or HTTP success is not authorization.

The authorization is consumed atomically before credential retrieval, after
scope validation. A failed/aborted attempt may have incurred a charge and does
not permit reusing that authorization. A new attempt requires deliberate new
authorization; the library does not grant it. This is a one-attempt data/cost
permission seam, **not a currency ceiling**: price and final billing are unknown.
Caller-owned session/account budgets must not interpret null as zero.

V04 owns total session scheduling, concurrency budgets, `Started` delivery,
Stop/epoch advancement and discarding stale results before downstream LLM/TTS.
The adapter does not silently stamp a completed response with a new epoch.
Use original context to reject stale output even if cancellation raced after
the final return. No partial-STT feature or complete orchestrator is claimed.

Example of consuming an **already authorized** request (names below represent
caller-supplied values, not defaults or generated consent):

```csharp
// Retain one adapter for the service lifetime; dispose after in-flight work ends.
using var adapter = OpenAiTranscriptionAdapter.Create(credentialSource);
var audio = BoundedWaveAudio.FromPcm(pcmFormat, boundedUtterance);
var result = await adapter.TranscribeAsync(
    context, selectedUpstreamModelId, audio, displayedLimits,
    authorizationFromApprovedCallingPath, stopToken);
// Recheck current request/epoch before using result.Text or its terminal event.
```

## Transport, lifetimes and hard limits

The fixed production origin is `https://api.openai.com:443`, with no userinfo,
path override, query or fragment. Endpoint construction is internal and fixed.
The safe factory owns `SocketsHttpHandler` with redirects and cookies disabled,
no handler/default-proxy credentials, no decompression, bounded response headers,
five-minute pooled connection lifetime, and unchanged platform TLS validation.
It does not modify machine proxy/TLS settings or bypass certificate validation.
Every 3xx, including same-origin redirects, fails without following `Location`.

Authorization is placed only in that request's Bearer header, never default
headers, a URL/query, multipart, diagnostics or a configuration file. Each
resolved credential is disposed after its request; disposal drops its reference,
but **does not guarantee erasure of managed strings or HTTP-internal copies**.
No secret is persisted by this library. Keep source implementations and HTTP
instrumentation from logging keys, bodies or result content. The library emits
no ordinary logs. Do not log HttpRequestMessage, Core content events, audio or
transcripts; external process-wide tracing is the host application's boundary.

Request, response, multipart/file content, response stream, linked tokens and
credential are disposed per call. The adapter/HttpClient is reusable for
concurrent independent requests if the injected credential source is safe for
that use. Dispose only after canceling/awaiting outstanding calls. No unbounded
queue or tasks are created; aggregate concurrency is the orchestrator's budget.

| Resource | Default / hard ceiling |
| --- | --- |
| WAV file including header | 8,640,044 bytes (below Core's 16 MiB capability cap and upstream 25 MB) |
| Audio | Nonempty mono PCM16 little-endian; 16/24/44.1/48 kHz, at most 90 seconds; exact sample-based duration checks |
| Accepted WAV layout | RIFF/WAVE, one 16-byte `fmt ` PCM chunk, one aligned nonempty `data` chunk; no ancillary metadata |
| Response body | 256 KiB; stop at limit + 1 even without Content-Length; verify declared length and require application/json |
| Transcript | 16,384 UTF-16 characters, matching Core per-event bound; caller may lower |
| JSON | Existing Core parser: depth 16, strict UTF-8/surrogates, no duplicate properties, comments or trailing content; validate ignored fields too |
| Languages / usage | At most 16 unique bounded language-code tokens; nonnegative integer token counts <= 1 billion with consistent total; reported duration <= 3,600 seconds. Unrecognized/missing billing variant is explicitly unknown. |
| Request duration | Default 60 seconds / hard 120 seconds, also bounded by request deadline and authorization expiry |
| Retry-After | Optional advice clamped to 0-300 seconds, never an automatic retry |

The request deadline is converted once to a relative timer; `TimeProvider` makes
timeouts deterministic in fixtures and timers monotonic in production. The
linked token covers credential retrieval, multipart upload, header wait and
bounded body read. The source must honor it. Caller cancellation takes precedence
over deadline expiration. Synchronous guards also check monotonic elapsed time
against the original request window and authorization window, and check the
absolute authorization expiry. They run after credential retrieval, before
send, at serializer entry and after response awaits. Delayed timer callbacks
cannot authorize a late upload, and a wall-clock rollback cannot extend the
original permission window. A pre-upload synchronous authorization cutoff is
`ConsentExpired`; a request-window cutoff is `DeadlineExceeded`. Timer-triggered
in-flight aborts remain deadline failures. A late success is not published.

The adapter issues exactly one `SendAsync`; a single-send content wrapper refuses
upload reserialization. HTTP/1.1 exact is used, not a streaming/upgraded inference
protocol. No application inference retry, disconnect recovery, model fallback,
or automatic inference readiness probe occurs. Aborting local HTTP does **not**
establish that provider compute stopped, that an uploaded recording was deleted,
or that a charge was avoided.

## Outcomes, errors and evidence

HTTP 200 still requires bounded, valid JSON with string `text`.
Whitespace/empty text normalizes to `NoSpeech`, not a fabricated transcript or
proof of acoustic silence; upstream hallucinations cannot be detected here.
Missing/null/wrong-type text, malformed JSON or unsupported schema is `Failed`.
Language absent/null/empty is unknown, never inferred from the transcript.
Confidence remains null; token logprobs are not calibrated confidence.

Recognized token/duration usage is preserved as reported; missing/new variants
are `Unknown`. Estimated cost is always null, including failures and no-speech.
Known but malformed usage is a schema failure. No raw vendor body, error message,
prompt, request ID/header value or echoed audio/text is copied into metadata.

`ProviderFailureCode` is provider-owned detail, with fixed authored messages and
existing Core errors at `Stage.Transcription`:

| Detail | Existing Core mapping |
| --- | --- |
| Missing/mismatched/expired/consumed consent; wrong origin/binding | `NotConfigured` |
| Unsupported or upstream missing model | `ProviderCapability` |
| Local audio/response size | `PayloadTooLarge` |
| Upstream audio format rejection | `AudioFormatUnsupported` |
| Malformed/unsupported response | `InvalidContract` |
| Declared-length mismatch / HTTP response-ended failure | `StreamTruncated` |
| Deadline | `DeadlineExceeded` (failed Core event, distinct adapter outcome) |
| Auth, permissions, quota, rate, rejected request/redirect, network, server | `ProviderFailed`, precise provider-owned detail retained |

Malformed/oversized optional error bodies do not erase known HTTP error status;
exact recognized `error.code` values refine quota/model/format only. Unknown
messages are not parsed heuristically. Response-scope read transport failures
also preserve non-success HTTP status/remedy and bounded Retry-After advice;
successful HTTP 200 response-ended failures remain `ResponseTruncated`.
Cancellation and synchronous/timer deadlines take precedence over that status.
Core `Retryable` is always false: advice or a transient failure does not authorize
a repeat charge.

An internal friend-assembly handler seam exercises the **same** adapter,
multipart serializer, authorization owner and Core-backed response parser. It
always labels results `Fixture` and cannot configure the public production
factory. Production results use `Live` for the actual local attempt/boundary;
even a locally blocked attempt is not inference readiness. Only a completed,
separately authorized live smoke could provide real inference evidence.

## Reproduction and remaining gate

Use SDK 10.0.401, existing centrally pinned test packages, committed generated
lock files and the dedicated pinned, read-only `providers.yml` workflow.
Shared solution registration belongs to the integration owner; until registered,
build/test this project directly. Ordinary restore can access NuGet; test
execution uses only in-process authored fixtures and does not access a network,
OS credential store, mic, playback, AI/model service or GPU.

```powershell
$env:CI = "true"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = "false"
# Use a unique C: output directory on the pooled-drive development host.
$artifacts = Join-Path $env:TEMP "martlet-provider-artifacts"
dotnet restore tests\Martlet.Providers.Tests --locked-mode --artifacts-path $artifacts
dotnet build tests\Martlet.Providers.Tests --no-restore -c Release --artifacts-path $artifacts
dotnet test tests\Martlet.Providers.Tests --no-build -c Release --artifacts-path $artifacts
```

Tests inspect actual multipart bytes and synthetic key placement; reject wrong
origin/consent/model/credential and malformed/oversized WAV; cover cancellation,
late replies, deterministic deadline phases, streamed/declared response bounds,
UTF/schema failures, no-speech/unknown language/usage, status-specific sanitized
errors, retry advice and no retries/fallback; and feed real normalized events
into Core's production sequence validator. Fixture audio is generated synthetic
PCM, not recorded speech; responses are authored, not copied provider assets.
No third-party code/assets are copied and no project license is granted.

Independent review identified two boundary defects in the first PR head:
deferred timer delivery could permit upload after credential lookup, and a
throwing error-body stream could overwrite a known HTTP failure. Production-path
regressions were run **before** fixes: 10 failed, 1 passed (the HTTP 200 truncation
control). The fixes and expanded 23-case regression group pass with all 168
existing cases, **191 total**, including callback deferral, UTC forward/rollback,
serializer-entry expiry, 401/403/429 versus 200 throwing streams, and stop
precedence. This remains offline fixture evidence, not a live authorization.

**Still NOT RUN:** real model/account eligibility, transcription accuracy and
latency, retention/account controls, actual billing, TLS/network behavior on a
real endpoint, consumer Windows capture/playback, voice orchestration and G2.
Before any live smoke, the owner must separately approve the exact model/account,
audio disclosure, cost tolerance/limits and credentials through the future
authorized path. Ordinary CI must never request those credentials or perform
inference to turn this fixture gate green.

## V03b: bounded text Responses streaming

`OpenAiTextGenerationAdapter` implements **streaming only**:
`POST https://api.openai.com/v1/responses`. It does not implement Chat
Completions, Realtime, a universal "/v1 compatible" transport, non-streaming
generation, a provider conversation store or an agent/tool executor.

### Dated upstream contract

Primary sources accessed **2026-09-12 (America/Los_Angeles)**; source inspection,
not service observations:

| Source | Consequence |
| --- | --- |
| [Create Response](https://developers.openai.com/api/reference/resources/responses/methods/create), [streaming events](https://developers.openai.com/api/reference/resources/responses/streaming-events), [streaming guide](https://developers.openai.com/api/docs/guides/streaming-responses) | Typed lifecycle, output/item/part, delta/done, refusal, incomplete and error events; done snapshots are not additional deltas. |
| [GPT-4.1 Mini](https://developers.openai.com/api/docs/models/gpt-4.1-mini) | Documents non-reasoning text output, Responses streaming and snapshot `gpt-4.1-mini-2025-04-14`. The adapter accepts **only that exact dated ID**, not its floating alias or another model family. Documented context/output capacity is larger than this adapter's local subset. |
| [Official Python request types](https://github.com/openai/openai-python/blob/e12b81d3bbf644ec7045e152d69bc4b68d69cd48/src/openai/types/responses/response_create_params.py), [text-only easy messages](https://github.com/openai/openai-python/blob/e12b81d3bbf644ec7045e152d69bc4b68d69cd48/src/openai/types/responses/easy_input_message_param.py), [event union](https://github.com/openai/openai-python/blob/e12b81d3bbf644ec7045e152d69bc4b68d69cd48/src/openai/types/responses/response_stream_event.py) | Immutable schema reference `e12b81d3bbf644ec7045e152d69bc4b68d69cd48`. No SDK, generated source, tokenizer, model or asset is installed/copied. |
| [Official Node Responses types](https://github.com/openai/openai-node/blob/e69eb4e4a88e1322e82385d4c119cef30c298dff/src/resources/responses/responses.ts), [SSE transport](https://github.com/openai/openai-node/blob/e69eb4e4a88e1322e82385d4c119cef30c298dff/src/core/streaming.ts) | Independent type cross-check at `e69eb4e4a88e1322e82385d4c119cef30c298dff`. An exact `[DONE]` can end SDK transport, but is not a successful Responses lifecycle event. |
| [Data controls](https://developers.openai.com/api/docs/guides/your-data), [background](https://developers.openai.com/api/docs/guides/background), [prompt caching](https://developers.openai.com/api/docs/guides/prompt-caching) | `store:false` and `background:false` are explicit. No training by default does not mean no retention: abuse monitoring, caching and account-specific controls are separate. No ZDR/MAM approval, retention exemption, disabled caching or deletion guarantee is claimed. |

Requests explicitly contain `stream:true`, `store:false`, `background:false`,
`tools:[]`, `tool_choice:"none"`, `parallel_tool_calls:false`,
`text.format.type:"text"`, `truncation:"disabled"` and `max_output_tokens`.
Optional caller personality is `instructions`; history is a bounded list of
`user`/`assistant` messages with string content, followed by current user text.
There are no arbitrary request dictionaries, remote prompt references,
`conversation`, `previous_response_id`, images, audio, reasoning configuration,
code execution, tools, model discovery or implicit history.

### Public API and authorization

| Surface | Contract |
| --- | --- |
| `TextModelSelection(ModelAlias, UpstreamModelId)` | Core alias is separate from the explicit upstream identifier. Catalog validation is passive and `NotRun`, not account access or readiness. |
| `BoundedTextInput(userText, personality?, history?)` | Owns immutable strings and a bounded copy of immutable history messages. Validates nonempty current input, UTF-16, controls, UTF-8 size and history count before any provider activity. |
| `TextGenerationLimits` | Immutable value object covering local input admission, requested output tokens, context, raw SSE/event/text bounds and deadlines. Every field must equal the authorization's limits. |
| `TextDisclosureAuthorization` | Single-use text-disclosure **and potential-charge** permission. Exact approved origin, LLM role, upstream model, alias, all three correlation IDs, original epoch, limits and expiry are bound. An STT authorization is not accepted. |
| `Create(credentialSource, timeProvider?)` | Safe reusable production HttpClient with owned nonredirecting, normal-TLS handler. No public arbitrary handler/client/endpoint override. Injected credentials must match origin/LLM/model exactly. |
| `Stream(context, model, input, limits, authorization, token)` | Captures the original monotonic/UTC request window now; returns a lazy `TextGenerationStream`. No secret is resolved or body serialized until enumeration after scope validation and atomic consent consumption. |
| `TextGenerationStream.GetAsyncEnumerator(token)` | **One enumeration only.** First event is local `Started`; subsequent pulls perform one HTTP attempt. Caller and enumerator cancellation are combined. Concurrent `MoveNextAsync`/disposal on the same enumerator is unsupported; one serialized consumer owns it. |
| `TextGenerationStream.Capabilities` | Core-compatible local attempt envelope (`Fixture` for injected fixtures, `Live` for the production boundary), LLM text deltas, request-abort capability. This is not a successful inference probe. |
| `TextGenerationStream.Result` | Null until terminal or enumerator disposal. Contains original context, outcome, nullable usage/cost, sanitized failure and separate refusal content. It does **not** collect/replay full user-visible text. |
| `TextGenerationResult.ToTurnResult()` | Existing Core outcomes; incomplete/output-limit/deadline map to `Failed`, refusal to `Refused`, Stop to `Canceled`. No shared Core schema changes. |

Only a calling path that actually obtained permission constructs authorization.
The library cannot authenticate human consent or defend against malicious code
in its process. Credentials/settings are not consent. Failed/aborted requests
consume their permission; there is no automatic renewal, retry or fallback.
Consent is for one attempted disclosure under limits, **not a monetary ceiling**.

Authorization and original request windows are checked synchronously after
credential lookup, before JSON construction, before HTTP send, at actual body
serialization and after every response await/before event acceptance. Absolute
consent expiry and original monotonic windows both apply, including delayed
timer callbacks and wall-clock rollback. Cancellation aborts outstanding HTTP
and disposes an acquired stream; late data is not accepted. Injected credential
sources must honor cancellation and translate expected store failures to
`CredentialUnavailableException`.

```csharp
using var adapter = OpenAiTextGenerationAdapter.Create(credentialSource);
var input = new BoundedTextInput(userText, approvedPersonality, approvedHistory);
var stream = adapter.Stream(context, selectedModel, input, displayedLimits,
    authorizationFromApprovedCallingPath, stopToken);
// Build the Core TextStreamRequest with original IDs/epoch and stream.Capabilities.
await foreach (var providerEvent in stream)
{
    // The owner checks current epoch, feeds its real ProviderSequenceValidator,
    // and drains the validator's independent user-text channel with backpressure.
    ConsumeOriginalEpochEvent(providerEvent);
}
var result = stream.Result; // metadata; no repeated full answer
```

Use `await foreach` (including early `break`) or dispose a manually acquired
enumerator. Abandoning a pull enumerator without disposal is a caller ownership
bug, not a supported cleanup mechanism. Early disposal marks the local result
`Canceled` without inventing a delivered terminal event. Normal terminals release
HTTP/request resources before being yielded. Adapter disposal cancels its active
requests; await/dispose their enumerators before discarding them. Independent
streams can intentionally share one adapter, provided the credential source is
concurrency-safe. Aggregate request/session concurrency remains V04's budget.

### Incremental protocol and outcome semantics

The supported **adapter subset**, not a universal provider guarantee, is one
assistant `message` at output index 0 with one `output_text` **or** `refusal`
part at content index 0. Item IDs, response ID/model, indexes, part type,
message role/status and terminal snapshots must agree. Missing/null message
phase or `final_answer` is allowed; commentary/unknown phases are rejected.
Reasoning items/parts/events, nonempty annotations, tools, images/audio and
unknown semantic event types fail closed. Such output never enters user text.

Accepted lifecycle: `response.created`, optional `response.in_progress`,
`response.output_item.added`, `response.content_part.added`, repeated matching
`response.output_text.delta` or `response.refusal.delta`, matching text/refusal
`.done`, `response.content_part.done`, `response.output_item.done`, and
`response.completed`. Intermediate done boundaries and a matching final snapshot
are required for success. Incomplete/failed/error may terminate earlier.

Upstream `sequence_number` must be present, nonnegative, bounded and strictly
increasing. **No upstream zero-start or gap-free guarantee is assumed.** A
duplicate or reordered event fails the attempt before replaying any content;
no reconnect/replay recovery is implemented. Independently generated Core
sequences are contiguous from `Started` at 0, then only emitted text deltas,
then one terminal. Done/final full text must equal accumulated deltas; it is
checked, never delivered twice. Missing deltas therefore cannot silently be
filled from a final snapshot. Partial text already displayed remains partial
if later validation fails; V04 must not relabel it completed.

| Upstream condition | Public/Core result |
| --- | --- |
| Valid nonempty text and matching `response.completed` | `Completed`; terminal has **no Text**, since deltas already supplied it. |
| Matching refusal part and completed response | `Refused`; buffered refusal is exposed only on the refusal terminal/result, never as a user-text delta. |
| `response.incomplete` / `max_output_tokens` | `OutputTokenLimit`, Core `Failed`; not a completed answer. |
| `response.incomplete` / `content_filter` | `Incomplete`, Core `Failed` with `ContentFiltered`; distinct from explicit refusal content. |
| `max_messages`, `steered`, missing/null or unknown incomplete reason | `Incomplete`, Core `Failed`. No WebSocket successor or hidden continuation. |
| `response.failed` or flat `error` | `Failed`; only recognized machine codes affect authored sanitized detail. |
| EOF/disconnect/truncation or exact `[DONE]` before valid terminal | `Failed`; never fabricate `Completed`. |
| Empty/whitespace completion, malformed/inconsistent/unsupported output | `Failed`, even after HTTP 200. |
| Caller/enumerator/adapter Stop | `Canceled`; preserve original IDs/epoch and suppress late content. |

SSE framing handles arbitrary UTF-8/network splits, LF/CRLF, optional initial
BOM, comments, optional event names and multiline `data:` joined by LF.
Named SSE event and JSON `type` must agree. SSE `id`, `retry` and extension
fields are bounded/ignored and cannot trigger reconnects. Strict Core JSON
validation covers ignored optional fields, malformed UTF, duplicates and depth.
Obfuscation/logprob metadata is not output text. No Chat Completions
`include_usage` assumption is made.

For ordinary chunked SSE, a valid semantic terminal closes the local stream
without waiting for physical EOF or `[DONE]`; later bytes are not interpreted.
When `Content-Length` is declared, its exact bounded length is checked before
publishing the terminal; only comments/empty frames and one optional exact
`[DONE]` may follow. This detects contradictory declared-length fixtures.
Truncated records, non-JSON sentinel suffixes, raw CR-only framing, unknown
`keepalive` JSON, missing required final snapshots, multiple messages/parts,
mixed text/refusal and alternative SDK recovery paths are **not supported**.
Their rejection is not evidence that the wider upstream API cannot emit them.

### Bounds, usage, content safety and error handling

| Resource | Default / hard ceiling |
| --- | --- |
| Input | 16,384 UTF-8 content bytes; 16 history messages, one current message, optional personality |
| Local input token admission | Content UTF-8 byte count plus 256 reserved units per message/personality; reservation <= `MaxInputTokens` (default/hard 24,576) |
| Output | `max_output_tokens` default 256 / hard 4,096; no retry to finish an incomplete answer |
| Local context budget | Input token reservation limit + output limit <= `MaxContextTokens` (default/hard 32,768); `truncation:disabled` |
| One raw SSE record | Default 128 KiB / hard 256 KiB, including comments/field framing; strict JSON depth 16 |
| Whole stream | Default 2 MiB / hard 4 MiB, plus at most one overflow-detection byte |
| Data events | Default 1,024 / hard 4,094 (leaves room for normalized Started/terminal under Core's 4,096 ceiling) |
| Accumulated text OR refusal | Default/hard 16,384 UTF-16 characters; caller may lower |
| Producer queue | None. One current record, two bounded record/line buffers, 4 KiB read-ahead, bounded snapshot text. No producer tasks or unbounded lists. |
| Time | First text/refusal delta 15 s, inter-event idle 10 s, total 60 s by default; each configurable up to 120 s, also bounded by original request deadline/consent expiry |
| HTTP optional error body | At most `MaxEventBytes`; known status retained if malformed, oversized or unreadable |
| Retry-After | Advisory only, bounded to 0-300 s; never authorizes a repeat request |

Input token reservation is **local conservative admission accounting**, not an
exact tokenizer result, provider-reported usage, cost estimate or a new REST
input-token-limit parameter. UTF-8 byte accounting and per-message reserve
bound the selected text-only input; no universal tokenizer compatibility is
claimed. Actual prompt framing/caching and billing remain provider-controlled.
Only supplied terminal usage counts are reported, subject to bounds and
consistent totals; missing/null usage stays unknown, never zero. Cache-write
details are bounded when present; nonzero reasoning usage is unsupported for
this non-reasoning subset. Estimated cost is **always null**.

First-delta and idle clocks begin at stream creation, so the earlier applicable
deadline also bounds credential/header waits. Comments do not reset progress.
Validated structural events reset idle but cannot postpone first delta;
consumer pauses still consume idle/overall/consent budgets. Once a content
delta arrives, idle and total deadlines continue. Slow consumers apply pull
backpressure instead of accumulating text/tasks.

The adapter does not speak or execute returned text. `text.format:"text"` is
not a guarantee that content contains no Markdown, URLs or instruction-like
text. Only designated assistant output is exposed; safe presentation and
sentence/markup filtering before **any** TTS belong to V04. Refusal has an
independent content channel. Never log inputs, Core content envelopes,
HttpRequestMessage or raw provider bodies. Public input/result/credential
`ToString()` and ordinary JSON metadata exclude content/secrets; explicit
Core text/refusal events are deliberately content-bearing.

LLM failures use existing Core codes at `Stage.Generation`. STT failure
messages/mapping remain unchanged. Known HTTP 401/403/429 survives an unreadable
optional body; cancellation and synchronous/timer deadlines take precedence.
All failure messages are authored locally, without echoed prompts/keys/vendor
messages or arbitrary request IDs. Aborting local HTTP is **request_abort**,
not proof of compute cancellation, deletion or avoided charges.

Shared V03a extraction is internal only: fixed OpenAI origin/handler policy,
single-send content, bounded optional-body read, Retry-After and original-window
guards. Existing credential ownership and error-code classification are reused;
there is no universal provider framework or relaxed STT contract.

### Offline evidence and remaining gates

The same direct-project locked restore/build/test commands above cover both
adapters under the existing read-only SHA-pinned provider CI. No package,
central pin, solution, runner, Core/settings or application-wiring change is
required. Tests author HTTP handlers, byte streams and Responses JSON rather
than storing downloaded provider content. They exercise the production
serializer, consent/credential boundary, transport, SSE reader, normalizer and
real Core sequence validator, including partial/error/refusal and original
correlation. All 191 STT regressions remain unchanged.

Local SDK 10.0.401 evidence: **323 provider cases passed** (191 existing STT,
132 new text cases), **43 Core contract cases passed**, and **66 Core sequence/
malformed-input cases passed**. All associated Release builds had zero warnings
and errors. The targeted selectors were `FullyQualifiedName~ContractTests` in
`tests\Martlet.Core.Tests` and
`FullyQualifiedName~ProviderSequenceValidatorTests|FullyQualifiedName~SequenceMalformedInputTests`
in `tests\Martlet.Fixtures.Tests`; each used locked restore, `build --no-restore`
and `test --no-build`. Outputs stayed under this session's unique C: artifact
directory, with process-local SDK/CLI environment only. These are authored
offline production-path tests, not measurements of the service.

**NOT RUN:** authorized live inference, actual account/model availability,
latency/accuracy/refusal frequency, prices/billing/retention settings, consumer
Windows behavior, TTS, end-to-end voice orchestration and G2. Fixtures do not
qualify any of these. V03c can reuse the narrow internal transport/credential
primitives; it needs its own speech/text-disclosure authorization, voice/format
contract and evidence. V04 owns current-epoch filtering, session budgets,
bounded validated text delivery, TTS staging and Stop. Neither is wired here.
