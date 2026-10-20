# Perception worker contracts

`Martlet.Perception` holds the contracts for the OCR and visual question
answering (VLM) workers of a host. OCR reads the text in a picture. A VLM
answers a question about a picture. The gateway (`Martlet.Gateway`) uses these
contracts for its `PerceptionOcr` and `PerceptionVlm` inference routes
(`GatewayInferenceRoute.Perception`).

The library has no screen capture, no network client and no model. The
Desktop feature [Watch my screen](SCREEN_COMMENTARY.md) captures the active
window itself and does not use this library.

An earlier selected-window capture engine, freshness scheduler, gateway client
adapter and synthetic worker transport were in this project. No app used them,
so they were removed (issue #942). Git keeps their history.

## What the library contains

| File | Contents |
| --- | --- |
| `PerceptionIdentities.cs` | `PerceptionWorkerIdentity`: the worker role (OCR or VLM), build, runtime, model, artifacts, limits, resources and cancellation capability |
| `PerceptionFrames.cs` | `PerceptionProtocol` limits and `SelectedWindowFrame` with its PNG or ephemeral-reference content |
| `PerceptionWorkerContracts.cs` | Job intent, vision authorization, worker request and response, OCR and VLM output, cancellation |
| `PerceptionFailures.cs` | `PerceptionWorkerFailure` codes, their catalog of remedies and `PerceptionWorkerException` |
| `DeterministicPerceptionFixtureIdentity.cs` | A synthetic worker identity for tests; it is not a model |

## Contract rules

`PerceptionProtocolVersion.Current` is worker protocol **1.0**, contract
`martlet.perception.worker`. It is not the gateway authentication protocol.
A worker identity match includes every field and artifact. There is no
detector role, compatibility fallback or model substitution.

`SelectedWindowFrame` binds an opaque selection, source and capture-permission
revision, frame ID, increasing capture epoch, timestamp and content. Content is
an immutable copied PNG or an opaque ephemeral reference:

| Worker boundary | Hard limit and behavior |
| --- | --- |
| Encoded image | 4 MiB, checked before copying |
| Dimensions | 4,096 px per edge, 8,294,400 pixels in total |
| PNG | 8-bit RGB or RGBA, not interlaced, exact rows, CRC and zlib checks, no unknown or trailing chunks |
| Ephemeral reference | Opaque identifier, digest, size, dimensions and expiry; no URL or path |
| VLM question | 512 characters or 1,024 UTF-8 bytes |
| OCR result | 128 bounded regions in order, an immutable snapshot, normalized bounds |
| Output text | 16 KiB UTF-8 or the smaller limit of the worker |
| Job and frame age | 15 seconds and 30 seconds at most; callers can set smaller limits |

`PerceptionJobIntent` binds one request, destination, worker, frame, task,
deadline and maximum frame age. Its `PerceptionVisionAuthorization` is No by
default, expires, is used only once and binds the exact request. It records the
decision of a trusted caller. It is not a human consent screen or an operating
system permission, and it does not authorize capture.

Failures use the typed `PerceptionWorkerFailure` codes, each with a stable
remedy. Do not show arbitrary worker error text to users. When vision is not
available, voice must continue without made-up context or an automatic retry.

## Tests

`tests\Martlet.Perception.Tests\ContractTests.cs` covers the protocol, the
identity pins, PNG checks, references, job and authorization rules and the
failure catalog. `Martlet.Gateway.Tests` covers the gateway perception routes.

```powershell
.\scripts\Test-Martlet.ps1 -Project Martlet.Perception.Tests
```