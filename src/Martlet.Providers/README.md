# V03a: named OpenAI bounded-file transcription

This is a production HTTP adapter library, **not a working voice application**.
It implements only `POST https://api.openai.com/v1/audio/transcriptions` with a
completed, bounded mono PCM16 WAV and one final JSON response. There is no
microphone, VAD, resampler, codec download, model download, LLM, TTS, onboarding,
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
over deadline expiration. Check tokens again after awaited boundaries, so a
handler returning success after cancellation does not publish it.

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
messages are not parsed heuristically. Core `Retryable` is always false: advice
or a transient failure does not authorize a repeat charge.

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

**Still NOT RUN:** real model/account eligibility, transcription accuracy and
latency, retention/account controls, actual billing, TLS/network behavior on a
real endpoint, consumer Windows capture/playback, voice orchestration and G2.
Before any live smoke, the owner must separately approve the exact model/account,
audio disclosure, cost tolerance/limits and credentials through the future
authorized path. Ordinary CI must never request those credentials or perform
inference to turn this fixture gate green.
