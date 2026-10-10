# Accounts and households

Status: **design and work plan**. The work plan below marks the workstreams
that shipped. Each workstream updates this page and the user guides when it
ships. This
page is the contract that the parallel workstreams share: change a contract
here (in the same PR) before code depends on a different one.

## Goal

Several people use Martlet in one home. Each person has their own companion:
their own characters, personalities and memories, so each person's character
seems like its own individual. The household shares its computers, hosts, paid
API keys, Home Assistant and the voices Martlet recognizes.

Requirements from the owner:

1. Each Windows sign-in (Microsoft account, work account or local account) is a
   Martlet user. It signs in automatically, with no password, until the person
   adds one.
2. A person can make a Martlet account and link any login to it: Windows
   logins, a Martlet password, Google, Microsoft, Authentik and other
   providers.
3. A local Windows account is a separate person until that person links it.
4. A person can switch accounts on one Windows login, and can add a new person.
5. Each account has its own characters, personalities and everything that goes
   with a character.
6. Each account has its own memories. Every host keeps every account's memories,
   because people share computers.
7. A person can share a character, and can share memories, with the household.
8. Voiceprints are always shared, so Martlet learns everyone's voice.
9. Paid API keys are shared in the household.

## Model

| Thing | What it is | Key |
| --- | --- | --- |
| Household | The [Martlet network](NETWORK.md): the hosts and devices of one home | Network ID (exists) |
| Account | A person. Owns characters, personalities, memories, history and preferences | Account ID |
| Login | One way to prove an account | `(kind, provider, subject)` |
| Device | One Windows user on one PC: a roster entry with its own network key | Device ID |
| Memory space | One memory store with one owner | Space ID |

Rules:

- A login proves an account. A voice never proves an account: a voice only
  tells whom a fact is about.
- An account has one or more logins.
- A device acts for an account only while that account is signed in on it.
- Linking a login to an existing account always needs a **Prove** sign-in
  (below), so a PC cannot take over someone else's account.

### Logins

| Kind | Proof | Checked by |
| --- | --- | --- |
| `windows` | Windows signed the person in | This device. Valid only on the device that made it |
| `martlet` | User name and password, optional authenticator code | Hosts, or a cached verifier on a PC where the account signed in before |
| `oidc` | Google, Microsoft, Authentik, Authelia, Keycloak, Pocket ID | Hosts (they keep the client secret) |
| `discord`, `steam` | Provider sign-in | Hosts |

Two strengths of sign-in:

- **Unlock** switches to an account that is already signed in on this device:
  the Windows login, a PIN, Windows Hello or the password. It works offline.
- **Prove** adds an account to a new device, links a login, signs in from
  outside home or changes household settings: the password (and the
  authenticator when set), a provider, or **Allow** on a device where the
  account is signed in (with the six-digit check number that joining uses).

A Microsoft or work e-mail on a Windows login is a **hint** only. When it
matches an account, Martlet asks *Continue as Sam?* and then needs a Prove
sign-in.

### Scopes

Every setting, file and synced document has exactly one scope.

| Scope | Holds | Changed by | Synced to |
| --- | --- | --- | --- |
| Device | Microphone, speakers, cameras, screens, terminal, local engines, pairings, network key | That device | Nowhere |
| PC | Companion or host PC, the host service on it, large downloaded models | Any Windows user of that PC | Nowhere (machine-wide folder) |
| Household | Hosts, who does what, work sharing, Thinking, Listening and Speaking routes **and paid API keys**, Thinking fallback, Home Assistant, updates, model abilities, People and voiceprints, speaking voices, character models and their emotes, the account directory | Owner and admins | Every host and household device |
| Account | Characters, personalities, character profiles, prompts, reply style, lorebooks, the character shown, how you talk, speech display, theme, Voice ID filter, reminders, creations, conversations, memory on or off | That account | Every host; only devices where the account is signed in |
| Memory space | Facts | See [Memory spaces](#memory-spaces) | Every host; only devices that may read the space |

Today's shared-settings sections:

| Household | Account |
| --- | --- |
| `thinking`, `listening`, `speaking`, `thinking-fallback`, `character-actions`, `voice-recognition`, `smart-home`, `updates`, `model-abilities`, `work-sharing`, `pc.*`, `role.*`, the voice list, speaking voices, character models, the cluster plan | `companion`, character profiles, `replies`, `prompts`, `memory`, `lorebooks`, `character`, `talk`, `speech-display`, `appearance`, `appearance-custom`, `voice-id`, `reminders.*` (per account, not per device), creations |

People and voiceprints sync even when *Keep Martlet the same on all my
computers* is off.

### PC scope

Built (W6). What the PC is for is the same for every Windows user of the PC:

- **Where.** The PC folder is `%ProgramData%\Martlet` when Martlet uses the
  default data folder (`%LOCALAPPDATA%\Martlet`). Martlet started with any other
  `--data-directory` (tests, MCP verification) keeps these files in that data
  folder, so it never changes the real PC. `MARTLET_PC_DIRECTORY` points several
  data folders at one PC folder for verification.
- **What.** `device-role.txt` (companion or host PC) and
  `this-pc-host-roles.txt` (the roles of this PC's host service, with the SID of
  the Windows user whose Docker Desktop Martlet read them in). No secrets.
- **Who may change it.** Martlet installs per user and never asks for
  elevation. The Windows user who makes the folder gives `BUILTIN\Users`
  *Modify*, inherited by its files, so every Windows user of the PC can change
  them.
- **Moving.** On the first start after the update, a Windows user's earlier
  `device-role.txt` (and host roles) moves to the PC folder when the PC folder
  has none. Each data folder keeps its own copy of the last choice made or
  followed there: it marks that this Windows user chose (or skipped the welcome
  tour), and an older Martlet reads it.
- **Following.** A running Martlet reads the PC folder every 30 seconds and
  follows a switch made by another Windows user. A switch asked from another
  computer (`role.<device ID>`) writes the PC folder, so every Windows user of
  the PC follows it. The `role.<device ID>` entry's time is the time the PC
  folder's choice last changed.
- **Welcome tour.** It shows when nobody on the PC chose yet, and for a Windows
  user who hasn't chosen yet on a companion PC. On a host PC a new Windows user
  lands on the host dashboard.
- **Host service.** Docker Desktop keeps its containers for the Windows user
  who runs it. A host PC starts the host roles by itself only in the Docker
  Desktop of the Windows user who ran them. Another Windows user sees *in
  another Windows user's Docker Desktop: sign in to Windows as that user to
  manage it*. The pairing with the host service stays with each device (Device
  scope).
- **Not built yet.** Large downloaded models (Ollama models, Parakeet, voice
  models) stay where each engine keeps them, mostly per Windows user.

MCP: `pc_scope` and the desktop's `DeviceRoleWhere` ([MCP](MCP.md)).

### Memory spaces

| Space | Owner | Read by default |
| --- | --- | --- |
| `account-<id>` | One account | All characters of that account |
| `character-<id>` | One character set to *its own memories*, or shared together | That character |
| `household` | The household | Every character |

- A fact keeps `voice_id`: whom the fact is about. The space tells who
  remembers the fact.
- Recall reads the active character's space, `household` and any space shared
  with the account. It loads them at sign-in or switch, so recall stays as fast
  as now.
- Every host keeps every space. A device downloads only the spaces it may read.

### Sharing

| Choice | Effect |
| --- | --- |
| Private (default) | Only the owner sees and uses the character |
| Share a copy | Household members can **Use a copy**: personality, look, voice, emotes and lorebooks are copied to their account, with its own memories |
| Share together | Household members talk to the same character. It uses a `character-<id>` space and remembers everyone |
| Share a fact | The Memory window moves or copies a fact to `household` or to another account's space |
| Share new memories about me | New facts about this person also go to `household` |

### Voices

The People list is one household list. A voice can link to one account
(*This is me* becomes *This voice is Sam*). When Alex speaks at Sam's PC, Sam's
character answers and saves the fact in Sam's space with Alex's voice ID. A
voice never switches the account, unlocks settings or spends money.

### Roles

| Role | May |
| --- | --- |
| `owner` | Everything. The current install becomes the owner |
| `admin` | Household settings, paid keys, invite and remove people, allow devices |
| `member` | Their own account; use household engines and keys |

### Flows

- **First start for a Windows user.** A device with an account binding signs
  that account in. Else, when the Windows e-mail matches an account, Martlet
  asks *Continue as ...?* and needs Prove. Else it makes a new account named
  after the Windows display name, bound to this Windows login, with no password.
- **Switch account** (avatar menu): accounts signed in on this device, *Sign in
  as someone else* and *Add a person*. Switching ends the conversation, stops
  listening and reloads the account. It never happens during a reply.
- **Account page**: add a password and an authenticator, link a provider, link
  or unlink this Windows login, *Ask for my password on this PC*, and *Merge
  another account into this one* (after Prove for both).

### Migration

1. The current install becomes the **owner** account.
2. Every existing member desktop binds to the owner account on update.
3. Today's personalities, profiles, prompts, memories, creations and reminders
   go to the owner's account. Facts keep their `voice_id`. The *This is me*
   voice links to the owner.
4. Each host's owner login and its `member` identities become logins of the
   owner account. Friends stay friends.
5. Hosts keep serving the old `/martlet/v1/memories` document as the owner's
   space to desktops on an older Martlet.

### Privacy

Accounts are kept apart, not secret from a person with administrator rights on
a host or PC. An account lock can encrypt that account's folder on a shared
Windows login. End-to-end encrypted spaces are later work.

### Latency

Sign-in, switching and space loading happen at start or at a switch, never in a
reply. Per-speaker context stays in the message notes. The start of each
Thinking request stays stable for each account.

## Shared contracts

All workstreams use these forms. Change them here first.

| Contract | Form |
| --- | --- |
| Account ID | `Guid`. In JSON as a normal `Guid`; in paths and space IDs as 32 lowercase hex digits (`"N"`) |
| Space ID | `household`, `account-<32 hex>` or `character-<32 hex>`; regex `^(household\|account-[0-9a-f]{32}\|character-[0-9a-f]{32})$` |
| Device ID | Existing devices keep theirs. New ones: `desktop-<pc name>-<6 of [a-z0-9]>`, at most 64 characters. Kept in `device.json` (`{"version":1,"deviceId":"..."}`) in the data folder, one per Windows user; `DeviceIds` in `Martlet.Core.Network`, `NetworkIdentity.ThisDevice` in the desktop |
| Windows login | `WindowsLogin.Current` in Martlet.Desktop: `Sid`, `Kind` (`microsoft`, `work` or `local`), `UserName`, `DisplayName` and `EmailHint`. The e-mail is a hint and personal data: never logged and never in MCP output |
| Login | `kind` (`windows`, `martlet`, `oidc`, `discord`, `steam`), `provider` (`windows`: the device ID; `martlet`: `martlet`; others: the household provider ID), `subject` (`windows`: the SID; `martlet`: the lowercase user name; others: the provider's subject). A `windows` login can belong to several accounts (*Add a person* binds each new person to the same Windows login); another kind belongs to one account, and on a conflict the account that added it first wins |
| Role | `owner`, `admin`, `member` |
| Account directory | `accounts.json` beside `network.json` on desktops and beside `host.json` on hosts. One last-writer-wins entry per account with the hybrid revision of [shared settings](CLUSTER.md#conflicts-and-offline-changes), signed by the writing member desktop's network key. Public facts only, never secrets. Format and rules: [Account directory](#account-directory) |
| Owner account ID | `OwnerAccount.IdFor(networkId)`: a UUID version 5 (RFC 9562) with the namespace `e44b5fc7-4473-46d2-9844-25457de2bfa8` and the name `"martlet-household-owner\n" + networkId` (one LF, no trailing newline). `"net-example"` gives `8a58da66-fddc-5c5b-9282-fb119c84915f`. Its `created_by` is the device that founded the network |
| E-mail hint | Lowercase hex SHA-256 of the UTF-8 text `"martlet-email-hint-v1\n<network ID>\n<trimmed, lowercase e-mail>"` (`Account.EmailHintFor`). The directory keeps only hints, never an e-mail address |
| Directory routes | `GET /martlet/v1/accounts`, `GET /martlet/v1/accounts/digest`, `POST /martlet/v1/accounts` (merge and return). Paired member devices only; friends are refused |
| Memory space routes | `GET /martlet/v1/memories/spaces/{space}`, `GET .../{space}/digest`, `POST .../{space}` (merge and return). The document is today's `SharedMemories`. The old `/martlet/v1/memories` keeps working. Answers name the `space`. A host keeps at most 64 spaces (`memories.spaces_full`). An access hook on the host (`GatewayMemorySpaces.Access`) may refuse a device (`memories.space_denied`); a POST needs read and write access. `Martlet.Core.Sync.MemorySpaceId` makes and checks space IDs. Client: `ReadMemorySpaceAsync`, `ReadMemorySpaceDigestAsync`, `MergeMemorySpaceAsync` |
| Creation routes | `GET /martlet/v1/creations/accounts/{32 hex}`, `GET .../digest`, `POST ...` (merge and return), `GET` and `POST .../chunks/{sha256}`. The documents are today's `CreationLibrary` and pieces; answers name the `account`. A host keeps one list per account (`creations-account-<32 hex>.json` on Linux) and one pool of pieces for every list, and deletes a piece only when no list's live creation uses it; at most 64 account lists (`creations.accounts_full`). Paired member devices only, and only those that `GatewayMemorySpaces.Access` lets use `account-<32 hex>` (read; a POST read and write), else `creations.account_denied`. The old `/martlet/v1/creations` stays for older desktops; the owner's desktop joins it with the owner's list both ways (`CreationOwnerBridge`). Desktop: the account folder holds `creations.json`, `creations\`, `creations-incoming\` and `creations-sync.json`; `<data>\accounts\creations-moved.json` records the one move of the data folder's creations (`CreationAccounts.MoveDataFolderCreationsOnceAsync`). Client: `ReadAccountCreationsAsync`, `ReadAccountCreationsDigestAsync`, `MergeAccountCreationsAsync`, `ReadAccountCreationChunkAsync`, `SendAccountCreationChunkAsync`; `HostCreationPeer.ForAccount` |
| Account attestation | `AccountAttestation` in `Martlet.Core.Accounts`: a host's signed statement that an account proved itself on a device. JSON (snake case): `schema_version` 1, `network_id`, `host_id`, `account_id`, `device_id`, `login` (an `AccountLoginKey`; never `windows`), `issued_at`, `expires_at` (1 minute to 30 days; 10 minutes by default), `algorithm` (`ES256` or `PS256`), `host_key` (base64url SubjectPublicKeyInfo of the host's **TLS key**, the key the roster pins as the host's `spki`) and `signature` over `"martlet-account-attestation-v1"` and the other fields, one per line (account ID as 32 hex digits, times as Unix milliseconds). `Check(roster, at)` accepts it only for an active host of that network whose pin is the SHA-256 of `host_key`, with a good signature, within its lifetime (5 minutes of clock skew). Its text form (`AccountAttestation.ToText()`, base64url of compact JSON, at most 4,096 ASCII characters) goes in a device binding's `attestation`. See [host sign-in](#host-sign-in-for-accounts) |
| Desktop account session | `AccountSession` in Martlet.Desktop: the current account ID, its folder `<data>\accounts\<32 hex>\`, the household folder (the data folder root) and an `AccountChanged` event raised only between replies |
| PC folder | `PcFolder` in `Martlet.Core.Installation`: `%ProgramData%\Martlet` for the default data folder, else the data folder itself, or `MARTLET_PC_DIRECTORY`. `BUILTIN\Users` may change it. Holds `device-role.txt` and `this-pc-host-roles.txt`; never secrets |

## Account directory

Built by W2. Code: `src\Martlet.Core\Accounts\` (`Account.cs`,
`AccountDirectory.cs`, `OwnerAccount.cs`), the host store and routes in
`src\Martlet.Gateway\GatewayAccountDirectory.cs`, the desktop's host client in
`src\Martlet.Avatar.Audio2Face\Remote\HostAccounts.cs`. The MCP tool
`accounts_sync_selftest` rehearses it end to end ([MCP](MCP.md)).

### Entry

`accounts.json` is JSON, snake case, schema 1, at most 2 MiB and 256 accounts:
`{"schema_version": 1, "accounts": [<entry>, ...]}`. One entry per account:

| Field | Form |
| --- | --- |
| `id` | The account ID (a normal `Guid`) |
| `name` | 1-64 characters, trimmed, no control characters |
| `role` | `owner`, `admin` or `member` |
| `logins` | At most 16: `kind`, `provider`, `subject`, optional `label` (at most 128 characters; shown, never trusted), `added_at` |
| `devices` | At most 64 device bindings: `device_id`, `login` (`kind`, `provider`, `subject`), `signed_in_at`, optional `attestation` (1-4,096 printable ASCII characters; the [account attestation](#shared-contracts) text) |
| `voices` | At most 16 voice IDs from People (32 lowercase hex digits) that are this person |
| `email_hints` | At most 8 [e-mail hints](#shared-contracts) for *Continue as ...?* |
| `sharing` | `characters` (`private`, `copy` or `together`) and `memories_about_me` (*Share new memories about me*) |
| `removed` | A removed account stays as a tombstone with its ID and name and no logins, devices, voices or hints |
| `merged_into` | Only on a removed account: the account it was merged into |
| `created_by` | The device that created the account. It never changes |
| `revision`, `updated_at`, `updated_by` | The hybrid revision, the time (UTC) and the writing desktop's device ID |
| `signature` | base64url ECDSA P-256 (IEEE P1363) by `updated_by`'s network key |

The signature covers every other field. The signed text is
`"martlet-account-v1\n"` and then each field on its own line: each text as
`<length>:<text>` (`-` for none), each number in decimal, each time as UTC
ticks, and the lists in sorted order. It does not include the network ID, so a
desktop can sign entries before it is in a network.

### Rules

- `AccountDirectory.Put(signer, account, now)` stamps and signs an entry. It
  keeps the `created_by` that the copy has. A new account takes the
  `created_by` it was given (the migrated owner takes the founder's device ID),
  or else the writer's device ID. A removed account can't change.
- `AccountDirectory.Merge(left, right)` keeps, per account, the entry with the
  highest (removed, revision, writer, content). A removal always wins, so a
  removed account never comes back. The merge is commutative, associative and
  idempotent.
- `AccountDirectory.Accept(current, incoming, roster)` takes the incoming
  entries that `AccountDirectory.Refusal(accepted, incoming, roster)` lets in,
  then merges. Hosts and desktops use it for every copy they receive.
  `Refusal` is the one place that decides; later rules (for example the binding
  rule of W12) go there. Today it refuses `account.no_network` (no roster),
  `account.signer` (not signed by an active member desktop with the key that the
  roster lists) and `account.creator` (the entry changes `created_by`).
- A host takes no entry while it is in no network. Entries it took earlier stay
  when their signer leaves the network, as with roster entries.
- A host keeps `accounts.json` when it leaves a network, as it keeps its other
  shared documents.

### Routes

| Route | Result |
| --- | --- |
| `GET /martlet/v1/accounts` | `host_id`, `digest`, `rejected` (0) and `accounts` (the directory) |
| `GET /martlet/v1/accounts/digest` | `host_id` and `digest` |
| `POST /martlet/v1/accounts` | Accepts and merges the posted directory; returns the same document as GET, with `rejected` set to the number of refused entries |

Only paired devices may call them, over their signed, pinned connection.
Friends (`access.friend`) and API keys are refused. The desktop client is
`Audio2FaceHostConnection.ReadAccountsAsync`, `ReadAccountsDigestAsync` and
`MergeAccountsAsync`. A desktop checks what a host returns with
`AccountDirectory.Accept` and its own roster.

### Lookups

- `Find(id)`, `Live`.
- `FindByLogin(login)`: the live account that added the login first.
- `AccountsWith(login)`: every live account with the login (several share a
  `windows` login).
- `SignedInOn(deviceId)`: the live accounts bound to a device, the latest
  sign-in first.
- `Account.Key` (32 hex digits) and `Account.SpaceId` (`account-<32 hex>`).

## Host sign-in for accounts

Built by W4. Code: `src\Martlet.Gateway\GatewaySignIn.cs` and
`GatewaySignInHttp.cs`, `src\Martlet.Core\Accounts\AccountAttestation.cs`, the
desktop client in `src\Martlet.Avatar.Audio2Face\Remote\HostSignIn.cs`. The
user guide is [NETWORK.md](NETWORK.md#household-accounts-on-a-host); the MCP
tool `signin_lab` with `mode` `account` rehearses it ([MCP](MCP.md)).

- `signin.json` keeps, as before, the owner login (always with an
  authenticator), providers and allowed identities, and now also
  `owner_account_id`, `accounts` (other accounts' `martlet` logins: account ID,
  user name, password verifier, optional authenticator and recovery codes) and
  `account_id` on allowed member identities. Files written before accounts
  load unchanged.
- The owner login and member identities without an `account_id` prove the
  owner's account: `owner_account_id`, else `OwnerAccount.IdFor` of the host's
  network. Friends prove no account (`signin.no_account`).
- A `martlet` login without an authenticator proves its account on a paired
  computer, but never adds a computer (`signin.needs_authenticator`). Outside
  access still needs the owner login or an allowed provider identity.
- **Prove route:** `POST /martlet/v1/signin/begin` (provider `martlet`, `owner`
  or a provider ID), then `POST /martlet/v1/signin/prove`, signed by the paired
  computer (not a friend's), with `attempt_id`, `proof` and optional
  `lifetime_seconds`. It answers `account_id`, `signed_in` and `attestation`
  for that computer. `/signin/complete` also answers `account_id` and
  `attestation` for the computer it adds. Desktop client:
  `Audio2FaceHostConnection.ProveWithPasswordAsync`, `ProveInBrowserAsync`,
  `BeginProveAsync` and `ProveAsync` (`HostAccountProof`), and
  `HostSignInIdentity.AccountId` and `Attestation` after joining.
- **Settings:** `POST /martlet/v1/signin/settings` actions `owner-account`,
  `account`, `remove-account-authenticator`, `remove-account`,
  `recovery-codes` with `account_id`, `allow` with `account_id` and `link`.
  `GET` answers `owner_account_id`, `accounts` (`account_id`, `user`,
  `has_authenticator`, `recovery_codes_left`) and `allowed[].account_id`, never
  a secret. Who may change them is unchanged: a member desktop of the network.
  Role checks (`AccountRoles.ManagesHousehold`) are W12's.

## Work plan

Each workstream is one session, one branch and one PR into `main`. Merges are
serial: refresh `origin/main` and reconcile before each merge. Shared files
(`CHANGELOG.md`, `docs/MCP.md`, `src/Martlet.Mcp.Protocol/McpServer.cs`, the
host client in `src/Martlet.Avatar.Audio2Face/Remote`, route registration in
`src/Martlet.Gateway`) get additive edits only.

| ID | Workstream | Owns | Needs |
| --- | --- | --- | --- |
| W1 | Device ID per Windows user; Windows login detection (SID, Microsoft, work or local, e-mail hint, display name). **Shipped** ([NETWORK.md](NETWORK.md#trust-model), device ID) | `LocalLogs.ThisDeviceId`, `HostSetup.SuggestedDeviceId`, `NetworkIdentity`, new `WindowsLogin.cs` | - |
| W2 | Account directory: contracts, merge, signatures, host storage and routes, client. **Shipped** ([Account directory](#account-directory)) | new `src/Martlet.Core/Accounts/`, new `GatewayAccountDirectory.cs`, new `HostAccounts.cs` client | - |
| W3 | Host memory spaces: storage, routes, access hook, client. **Shipped** ([MEMORY.md](MEMORY.md#memory-spaces-on-every-host)); the hook admits every member device until W9 | `GatewayMemories.cs` and new space files, `HostMemories.cs` | - |
| W4 | Host sign-in for accounts: several account logins in `signin.json`, identities linked to account IDs, account attestations. **Shipped** ([Host sign-in for accounts](#host-sign-in-for-accounts)) | `GatewaySignIn*.cs`, `GatewayAccounts.cs`, `GatewaySignInHttp.cs` | - |
| W5 | People always shared | `MainWindow.People.cs` voice sync | - |
| W6 | PC scope: companion or host role and host service machine-wide for all Windows users. **Shipped** ([PC scope](#pc-scope)) | `DeviceRole.cs` and its callers, `PcFolder.cs` | - |
| W7 | Desktop account session, owner migration, account picker, *Add a person*, first start, directory sync | new `AccountSession.cs`, new `MainWindow.Accounts.cs` | W1, W2 |
| W8 | Account-scoped settings: split `settings.json` and shared settings into household and account parts; characters per account | `AppSettingsSections`, `SharedSettings*`, character profiles | W7 |
| W9 | Desktop memory spaces: a store per space, recall over spaces, space sync, host access checks | `DesktopMemoryService`, `MainWindow.MemorySync.cs`, `MemorySyncNode` | W3, W7 |
| W10 | Sharing: character copy and together, the household space, fact sharing | Characters page, Memory window | W8, W9 |
| W11 | Voice-to-account links on People; Voice ID filter per account | People page, `voices.json` | W7 |
| W12 | Account security: password and authenticator per account, PIN and Windows Hello lock, sign in as someone else, remember on this PC, merge accounts | Account page | W4, W7 |
| W13 | Provider logins linked to accounts; new devices join by account sign-in | Sign-in windows | W4, W12 |

```mermaid
flowchart LR
  W1 --> W7
  W2 --> W7
  W7 --> W8
  W7 --> W9
  W3 --> W9
  W8 --> W10
  W9 --> W10
  W7 --> W11
  W4 --> W12
  W7 --> W12
  W12 --> W13
```

W1 to W6 run in parallel now. W7 starts when W1 and W2 merge. W8, W9, W11 and
W12 run in parallel after W7. W10 and W13 come last.
