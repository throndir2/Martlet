# Martlet's API for other apps and scripts

Your own computers never need a key: Martlet on them reaches your hosts over
paired, signed connections ([Martlet network](NETWORK.md)). Anything else
(Home Assistant, a script, a Stream Deck plugin, a future integration) uses an
**API key** that Martlet makes for it.

## Making and revoking keys

**Devices › Apps and API keys › Create API key**: name the key after the app
that uses it, choose what it may do and when it expires (never, 30 days, 90
days or a year). The key is shown **once**, with your hosts' addresses, their
key pins and an example request; copy it into the app's secret settings.
Martlet keeps no copy and can't show it again. **Revoke** stops it everywhere.
Each key row shows what it may do, which computer made it, when it was last
used and on which host, and the first characters of its ID.

A key looks like `martlet_<22-character ID>.<43-character secret>`. The fixed
`martlet_` prefix lets secret scanners spot a leaked key.

## Calling a host

| What | Value |
| --- | --- |
| Address | `https://<host address>:9443`, as listed when the key was made (also on Devices) |
| Key | `Authorization: Bearer martlet_...` on every request; never in a URL |
| Bodies | `Content-Type: application/json` (exactly, or with `; charset=utf-8`) |
| Role (optional) | `X-Martlet-Role: voice` or `perception` picks whose routes `capabilities` and `status` describe, and which role a cancel is for (default `voice`) |
| Errors | JSON `{code, summary, remedy, trace_id}` with a 4xx/5xx status |

Hosts use their own certificate, so pin the host's **public key**. It never
changes (the certificate renews every 90 days with the same key), and the
create dialog shows each host's pin in curl's form:

```sh
curl -k --pinnedpubkey "sha256//<pin>" \
  -H "Authorization: Bearer $MARTLET_API_KEY" \
  https://192.168.1.20:9443/martlet/v1/version
```

`-k` only skips the certificate authority check that a home host can't pass;
`--pinnedpubkey` still refuses any other server. Clients that can't pin can
skip certificate checks on a network you trust, but then anyone who can
impersonate the host there can read the key.

`GET /martlet/v1/version` answers any valid key with the host, its Martlet
version and the key's own `api_key` (`id`, `name`, `scopes`, `expires_at`),
so an app can check its key.

## What a key may do

| Scope (in the dialog) | Endpoints |
| --- | --- |
| any key | `GET /martlet/v1/version`, `GET /martlet/v1/capabilities` |
| `read` (See status and logs) | `GET /martlet/v1/status`, `GET /martlet/v1/machine` (hardware), `GET /martlet/v1/cluster` (who does what), `GET /martlet/v1/logs?after=N[&limit=L]`, `GET /martlet/v1/commands`, `GET /martlet/v1/commands/{id}` |
| `voice` (Use thinking, listening, speaking and lip-sync) | `POST /martlet/v1/inference/ollama-chat`, `/f5-synthesis`, `/transcription`, `/audio2face`, and `POST /martlet/v1/inference/cancel` for its own requests |
| `perception` (Use screen understanding) | `POST /martlet/v1/inference/perception/ocr`, `/perception/vlm` and cancel, on hosts that run them |
| `manage` (Update hosts and change their roles) | `POST /martlet/v1/commands` (`martlet.update`, `host.status`, `host.describe-role`, `host.add-role`, `host.remove-role`, `host.exposure`), `POST /martlet/v1/commands/{id}/cancel`, and reading commands |

A key can **never** pair devices, read or change your Martlet network, read
the voice list (it holds voiceprints), change who does what, upload logs, take
a host's commands as its agent, or read or change API keys; those answer
`key.scope` (403). Every request and response is the same as for your own
computers; the [gateway contract](../src/Martlet.Gateway/README.md#implemented-https-surface)
lists each body and bound.

| Code | Status | Meaning |
| --- | --- | --- |
| `auth.missing` | 401 | No key (or signed request) |
| `key.invalid` | 401 | Unknown key or wrong secret, or the host hasn't heard of the key yet |
| `key.revoked` / `key.expired` | 401 | Revoked or past its expiry |
| `key.scope` | 403 | The key doesn't allow this request |

### Using a host's engines

Engine routes take the host's exact route identity, so a reply always comes
from the model the host advertises:

1. `GET /martlet/v1/capabilities` and pick the route (for example
   `route_id` `martlet.gateway.ollama-chat.v1`).
2. `POST` to its `path` with its `route_id`, `contract_id`,
   `contract_version`, `destination_id`, `worker_id`, `adapter_version`,
   `model_id`, `model_revision`, `model_sha256` and `artifact_identity_sha256`
   copied in, plus `protocol_version` `{"major":2,"minor":0}`, fresh
   `session_id`, `turn_id` and `request_id` (UUIDs), `epoch` `0`, a
   `deadline_utc` within the route's `maximum_duration_milliseconds`, and the
   route's `payload`. For a chat: `{"input": "...", "temperature": 0.7,
   "maximum_output_tokens": 256, "maximum_context_tokens": 4096}` (optional
   `system` and up to 16 `history` messages `{role, text}`, and `think`
   `false` to answer without thinking first; a chat takes up to 15 minutes (a
   Deep thinking think, which has no time limit of its own, asks for all of it),
   32,768 output tokens and 32,768 context tokens, so long thinking fits). For
   transcription: `{"sample_rate": 16000, "pcm_base64": "..."}` (mono 16-bit,
   at most 30 s).
3. Read the `application/x-ndjson` reply: `started`, `text_delta` events with
   `text` (or audio/face frames), then `completed`, `failed` (with `code`) or
   `canceled`.

Each route runs one request at a time (`job.busy` while another runs), shared
with your own computers: an app that keeps a route busy delays your own
conversations on that host.

### Commands

```sh
curl -k --pinnedpubkey "sha256//<pin>" -H "Authorization: Bearer $MARTLET_API_KEY" \
  -H "Content-Type: application/json" -d '{"kind":"martlet.update","arguments":{"version":"0.18.0"}}' \
  https://192.168.1.20:9443/martlet/v1/commands
```

The answer (202) holds the command's `id`; follow it with
`GET /martlet/v1/commands/{id}`. It runs once Martlet runs on that host, as
[commands between your computers](CLUSTER.md#commands-between-your-computers)
describe; `requested_by` shows `api-key-<key ID>`.

## How keys reach your hosts

Keys belong to your Martlet network, like the [shared who does what](CLUSTER.md):

- Each desktop keeps the list in `api-keys.json` in Martlet's data folder;
  each host keeps it in `api-keys.json` beside `host.json` (0600, service
  owner). It holds names, scopes, expiry, who made each key and the SHA-256 of
  each secret; never a usable key.
- Every 30 seconds while Martlet runs (and right after you make or revoke a
  key, or pair a host) each desktop merges its copy with every paired host and
  gives each host whose copy differs the merged list. A key works on a host a
  moment after it reaches it; a host that was off gets it on its next sync.
- One entry per key, newest stamp wins, except that **a revoked key always
  wins**: an older copy can never bring it back. Revoking also stops a reply
  the key is streaming on a host that already heard about it.
- Only paired computers read or change the list (`GET`/`POST
  /martlet/v1/api-keys`, signed). Each host reports when each key was last
  used there since it started.
- At most 32 live keys and 64 entries counting revoked ones; hosts older than
  API keys ignore them (**Update host**).

## Security notes

- Keys are bearer secrets: whoever has one can use it until you revoke it. Give
  each app its own key with only the scopes it needs, and an expiry if it is
  temporary.
- Hosts listen on their private LAN address only. To reach them from outside
  your home, use a VPN into your network; don't forward the port to the
  internet.
- The engines a `voice` key uses run on your hosts, but what the app sends
  them (text, audio) is processed there and shows in the host's logs only as
  activity, never content.
- Your own computers keep using signed requests (HMAC with nonce and clock
  window over a pinned connection); keys add no way into pairing, the network
  or other computers.

### Why API keys

| Option | Verdict |
| --- | --- |
| **Scoped API keys** (bearer, over TLS with a pinned host key) | Chosen: every HTTP client, Home Assistant's REST integrations, n8n and scripts can send a header; revocable per app; nothing secret stored on hosts |
| Martlet's signed requests (HMAC, what your computers use) | Kept for your computers; too much code for most integrations (canonical request, nonce, clock) |
| OAuth 2.0 | Needs an authorization server and token refresh in every app; no benefit for keys you hand out yourself |
| Client certificates (mutual TLS) | Strong, but few integrations can install one, and rotating or revoking them is harder for owners |

## Where it lives

| Piece | Location |
| --- | --- |
| Key list, format and merge rules | `Martlet.Core.Access` (`ApiKeyList`, `ApiKeyToken`, `ApiKeyScopes`) |
| Host side | `Martlet.Gateway` `GatewayApiKeys.cs` (store, bearer checks, `/martlet/v1/api-keys`); scopes per endpoint in `GatewayHttp.cs`, `GatewayCommands.cs`, `GatewayLogs.cs`, `GatewayCluster.cs`, `GatewayInferenceHttp.cs`; `api-keys.json` on Linux hosts (`ControlApiKeyStorage`) |
| Desktop client | `Martlet.Avatar.Audio2Face` `Remote/HostApiKeys.cs` |
| Desktop UI and sync | `MainWindow.ApiKeys.cs`, `ApiKeyDialogs.cs`; the **Apps and API keys** card on Devices |
| MCP | `api_keys_status` (this PC's list, never a key) and `api_selftest` (`Martlet.NodeLinkCheck api`); card IDs in [MCP](MCP.md) |

## Qualification

Checked on the Windows development PC through Martlet MCP: `api_selftest`
(two real gateways on 127.0.0.1 with the real Ollama relay route over a
fixture Ollama, NOT AI: keys made and synced through the desktop client, hosts
keeping verifiers only, refusals without a key, with a wrong key and on every
endpoint keys may never use, read, voice and manage scopes, the documented
`curl -k --pinnedpubkey` request with Windows' own curl (and a wrong pin
refused), a chat through the native route with a bearer key, the same key on a
second host and after its restart, last-used reports, revoking mid-reply, a
stale copy and expiry), and
the desktop's Devices card on a disposable data folder (create with name,
scopes and expiry; the key shown once and never returned to MCP; revoke with
confirmation; `api_keys_status`). **NOT RUN:** a desktop syncing keys with real
paired hosts (it needs a real pairing secret in Windows Credential Manager),
`api-keys.json` on a native or Docker Linux host, a real model, Home Assistant
or another real integration, and two physical PCs on a real LAN.

Not built yet: simpler or OpenAI-compatible endpoints over the same keys, and
an API on the desktop itself (for example asking the companion to say
something).
