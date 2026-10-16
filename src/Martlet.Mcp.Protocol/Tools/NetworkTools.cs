using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Installation;
using Martlet.Doctor;
using Martlet.Mcp.Client;

namespace Martlet.Mcp;

/// <summary>martlet_guide's "Computers, network and accounts" area: paired computers, hosts, the network, sign-in and accounts.
/// Each Tool(...) registers one tool: its name, description, input schema, handler and, when it needs one, its permission (<see
/// cref="ToolPermission"/>). A new tool of this area goes here.</summary>
internal sealed partial class McpServer
{
    private McpTool[] NetworkTools() =>
    [
        Tool("host_connections_selftest", "Rehearse how the desktop connects to a paired host and reports its status, with the " +
            "production code: one real gateway on 127.0.0.1 (pinned TLS, signed requests) behind a loopback TCP forwarder stopped and " +
            "started at the same address, and a simulated desktop checking it as the 15-second sync does (a new paired connection per " +
            "check) and logging through the desktop's status tracker. Checks that twelve checks share one kept TCP and TLS connection " +
            "(connections dialed and accepted are counted), that one missed check is not reported, that a host that stops is logged " +
            "once as stopped answering with the refused address named, and once as answering again when it is back, that a host " +
            "missing every other check is never reported, and that after the host is paired again (the old secret deleted) a job " +
            "saved with the old pairing reaches it with this PC's current pairing. Returns the log lines. Loopback only; writes nothing.", new { },
            async (_, cancellation) => await NodeLinkCheckAsync(cancellation, "host-connections")),
        Tool("cluster_status", "Read shared \"who does what\" sync from a data directory: whether sync is on (on by default, " +
            "\"off\" only after the owner turned it off) and this PC's copy of the plan (each job's host, failover and which device " +
            "changed it last; each host's roles). Read-only; contacts nothing and returns no addresses or keys.", new
        {
            dataDirectory = new { type = "string" }
        },
            arguments => ClusterStatus(arguments)),
        Tool("network_status", "Read this PC's Martlet network from a data directory (network.json and whether its network key " +
            "exists): member, waiting for approval (with the check number) or in no network; the network ID; each desktop and host " +
            "in the roster (ID, name, removed, who changed it last); hosts paired on purpose (adopt) and forgotten here (ignored); " +
            "each paired host in hosts.json with how many outside addresses are kept with its pairing and its access (pairedHosts: " +
            "\"member\" for your own, \"friend\" for a host a friend shares with this PC, used for its engines only and never in this " +
            "PC's network); and who your hosts are shared with as Share › Friends last read it (friends: per host, each friend's " +
            "label, provider and how many of their computers signed in, and how many asked; friends.json); and the household's sign-in " +
            "providers as the desktop last read them from its hosts (householdSignIn: each provider's ID, kind and name, the hosts that " +
            "have it, miss it, have other settings or lack its client secret, and whether that desktop keeps its secret to add it to " +
            "new hosts; household-signin.json); the share links this PC made and accepted (shareLinks: each made link's friend name " +
            "and whether it covers all hosts or which, each accepted link's sender and the hosts it signed in to or you forgot, whether " +
            "martlet:// links open Martlet for this Windows user, and whether a clicked link waits for the running Martlet; " +
            "share-links.json, never a link ID or secret); and the port Martlet opened on the router for this PC's host service " +
            "(routerPort: host, router name, external and internal port, when it was opened and renewed, the last problem; " +
            "router-port.json, never an address). Also this PC's device " +
            "ID (device: id and source, saved in device.json, one per Windows user; source pairings or legacy is the ID the desktop " +
            "keeps on its next start, new means it picks desktop-<pc name>-<6 characters>) and the Windows login it runs under " +
            "(windowsLogin: kind microsoft, work or local, and whether it has an e-mail hint; never the e-mail, name or SID). " +
            "Read-only; contacts nothing and returns no keys, secrets or addresses.", new
        {
            dataDirectory = new { type = "string" }
        },
            arguments => NetworkStatus(arguments)),
        Tool("account_security_status", "Read account security from a data directory (docs/ACCOUNTS.md): per account in accounts.json " +
            "its role, whether it was removed or merged (mergedInto), how many logins of each kind it has (windows, martlet, oidc, " +
            "discord, steam), whether it has this PC's Windows login, and each device binding (device ID, login kind, whether it " +
            "carries a host attestation, and its proof under the binding rule: creator, attestation, migration, merged, or none when " +
            "a host or desktop would refuse it); the accounts signed in on this PC; and per account with an unlock file here " +
            "(account-locks\\<id>.json, this PC only) the ways it unlocks (pin, password, hello), Ask for my password on this PC, " +
            "Remember me, Encrypt my files while locked with how many of its files are encrypted and plain, wrong tries and a wait. " +
            "Read-only; contacts nothing; never a PIN, password, verifier, attestation, e-mail, login user name or SID.", new
        {
            dataDirectory = new { type = "string" }
        },
            arguments => AccountSecurityStatus.Read(DataDirectory(arguments))),
        Tool("accounts_status", "Read who uses Martlet on a desktop's data directory (docs/ACCOUNTS.md): this device's ID; the " +
            "account session (accounts\\session.json: none, loaded or unreadable; whether its Windows login is the " +
            "MARTLET_SIMULATE_WINDOWS_LOGIN fixture), the account in use and each account signed in on this device (ID as 32 hex " +
            "digits, display name, role, whether it still waits to reach the household's directory, whether its folder " +
            "accounts\\<id> exists), the household's owner account; and this PC's copy of the account directory (accounts.json: " +
            "how many accounts, removed and owners, its revision, and per account its ID, name, role, login kinds, how many devices " +
            "it is signed in on, whether this device is one, who created and last changed it, and when). Never a SID, e-mail, " +
            "signature or attestation. Read-only; contacts nothing.", new
        {
            dataDirectory = new { type = "string" }
        },
            arguments => AccountsStatus.Read(DataDirectory(arguments))),
        Tool("outside_reachability_check", "Check how each host in this PC's Martlet network (network.json in a data directory) can " +
            "be reached: its home address and each owner-set outside address (overlay or port forward), each dialed directly, checked " +
            "against the host key pinned in the roster and asked GET /health/live (no credential, nothing else). Returns per host which " +
            "address answered, in how many ms, why the others didn't (refused, no answer in time, name not found, another key) and which " +
            "one a desktop would use (home first). Contacts the owner's hosts only when contactHosts is true; otherwise it lists what it " +
            "would check. Returns host IDs and address numbers, never the addresses.", new
        {
            dataDirectory = new { type = "string" },
            contactHosts = new { type = "boolean" }
        },
            async (arguments, cancellation) => await OutsideReachabilityAsync(arguments, cancellation)),
        Tool("network_selftest", "Rehearse the Martlet network end to end with the production code: three real gateways on " +
            "127.0.0.1 (pinned TLS, volatile credentials) and two simulated desktops using the desktop's network client and sync " +
            "engine (found, bind hosts, join with a check number, pair every member with every host by itself, refuse forged keys " +
            "and rosters, remove a desktop and a host). Loopback only; writes nothing to disk or the credential vault.", new { },
            async (_, cancellation) => await NodeLinkCheckAsync(cancellation, "network")),
        Tool("signin_lab", "A live sign-in lab for the desktop on a disposable data directory, so the Sign-in from outside " +
            "window and Join with an invite can be driven against a real host. action \"start\" with mode \"owner\" (the default): a " +
            "real gateway on 127.0.0.1 (Martlet.NodeLinkCheck signin-lab) with an owner account, an OpenID Connect provider (an " +
            "issuer in that process) and an allowed identity; it pairs the desktop of dataDirectory (hosts.json there; the secret in " +
            "the lab credential folder of MARTLET_LAB_CREDENTIALS, never Windows Credential Manager, so the MCP server and desktop " +
            "must run with Invoke-MartletMcp.ps1 -LabCredentials), a simulated laptop signs in with that identity and keeps syncing " +
            "the network, and a simulated friend's computer (lab-friend-7, ana@example.net) signs in every 3 seconds until the owner " +
            "allows it as a friend in the window, then reports what the host gives it. mode \"friend\": the desktop of dataDirectory " +
            "is the friend: the lab host belongs to a simulated owner, runs a fixture Ollama Thinking route and allows the lab " +
            "identity as a friend; status carries the invite to paste in Join with an invite, and the lab's simulated browser " +
            "answers the desktop's sign-in (MARTLET_LAB_BROWSER, set by -LabCredentials); with signInDesktop true (for a locked or " +
            "headless session, where the window can't be driven) the lab signs the desktop in as the friend itself, under the device ID " +
            "that desktop names itself by, and keeps the pairing as Join with an invite does (hosts.json with access friend), so the " +
            "desktop started afterwards on that data directory runs with a shared host. \"status\": what the lab sees (owner mode: " +
            "whether the desktop bound the host, the laptop's membership and events, the friend's access, the engines it reaches and " +
            "the refusals elsewhere; friend mode: whether the desktop signed in as a friend, its access, whether it ever asked to " +
            "join or was refused anything (access.friend), and its Thinking requests). With shareWithFriend true (owner mode, for a " +
            "session without the window) the lab's own admin desktop shares the host with the simulated friend from the start, so the " +
            "running desktop meets a friend's computer in its network sync. mode \"account\" (headless, no desktop needed): household " +
            "account sign-in on a host with an ECDSA key; its owner starts a network and sets up the owner login, " +
            "Sam's (password and authenticator), Alex's (password only) and a provider identity linked to Sam, then every Prove " +
            "sign-in (POST /martlet/v1/signin/prove) and a laptop's sign-in as Sam must give a host attestation that " +
            "Martlet.Core's AccountAttestation.Check accepts against the roster, and a changed or expired attestation, another key, " +
            "a friend's identity (signin.no_account), a wrong password and Alex adding a computer (signin.needs_authenticator) are " +
            "refused; status has ok, checks and attestations. With household true (mode \"account\"; needs the desktop of " +
            "dataDirectory and Invoke-MartletMcp.ps1 -LabCredentials): household sign-in providers instead: two real gateways " +
            "(lab-host-a and lab-host-b, both routed to the lab issuer) paired with that desktop; the lab's admin desktop set the " +
            "household provider authentik up on lab-host-a only (a public client), so the desktop adds it to lab-host-b by itself " +
            "when it reads its hosts' sign-in settings, and Save provider in Sign-in from outside saves it on both; status says " +
            "which hosts have it (providerOnHosts) and keep a client secret (clientSecretOnHosts), never the secret; once both are bound, " +
            "acting for that desktop it links a provider login to Sam, and two new computers join as Sam's (newDeviceMember, " +
            "secondDeviceMember); it answers the desktop's own browser sign-ins (MARTLET_LAB_BROWSER: first as the desktop's person, for " +
            "the Account page's Link, then as Sam, for Sign in as someone else). With joinDesktop " +
            "true (mode \"account\"; the desktop of dataDirectory and -LabCredentials): that desktop is a new computer that joins a " +
            "household as Sam's: the lab host belongs to a simulated owner who linked the lab provider login to Sam, the lab signs the " +
            "desktop in as Sam for it (as Join with an invite does, under its device ID: hosts.json and joined-account.json), and once " +
            "the desktop reads hosts.json the owner's computer lets it in by the host's attestation; status says askedToJoin, " +
            "joinRequest (with account), desktopMember and joinedAccountWaiting. \"stop\": " +
            "ends it (it also ends with this server).", new
        {
            action = new { type = "string", @enum = new[] { "start", "status", "stop" } },
            mode = new { type = "string", @enum = new[] { "owner", "friend", "account" } },
            signInDesktop = new { type = "boolean" },
            shareWithFriend = new { type = "boolean" },
            household = new { type = "boolean" },
            joinDesktop = new { type = "boolean" },
            dataDirectory = new { type = "string" }
        }, ["action"],
            async (arguments, cancellation) => await SignInLabAsync(arguments, cancellation)),
        Tool("role_lab", "A live lab for switching your computers between companion and host PC, for the desktop on a disposable " +
            "data directory. action \"start\": a real gateway on 127.0.0.1 (Martlet.NodeLinkCheck role-lab; the network and shared " +
            "settings in memory) paired with the desktop of dataDirectory under the device ID that desktop names itself by (hosts.json " +
            "there; the secret in the lab credential folder of MARTLET_LAB_CREDENTIALS, so run Invoke-MartletMcp.ps1 -LabCredentials), " +
            "and a simulated companion PC (lab-companion) that asks to join the desktop's network once it is bound, syncs the shared " +
            "settings every 2 seconds, says what it is (pc.lab-companion) and follows an ask that it switch (role.lab-companion). " +
            "\"ask\" with role host or companion: the simulated PC asks the desktop to become that (role.<desktop device>). \"status\": " +
            "the simulated PC's role and membership, what the host's copy says each computer is and the newest ask about each, and " +
            "its events. \"stop\": ends it (it also ends with this server).", new
        {
            action = new { type = "string", @enum = new[] { "start", "status", "ask", "stop" } },
            role = new { type = "string", @enum = new[] { "host", "companion" } },
            dataDirectory = new { type = "string" }
        }, ["action"],
            async (arguments, cancellation) => await RoleLabAsync(arguments, cancellation)),
        Tool("signin_selftest", "Rehearse joining from outside home by signing in, end to end with the production code: a real " +
            "gateway on 127.0.0.1 (pinned TLS, in-memory signin.json and network.json), a member desktop at home that sets up the " +
            "owner account (password plus a real authenticator secret and recovery codes) and makes an invite, and a laptop that " +
            "only has the invite: it pins the host (reached by name, so only the pin is trusted), is refused with a wrong password, " +
            "a reused code and a forged pin, signs in, asks to join and is let in by the home PC on the host's sign-in attestation " +
            "with no check number; then an OpenID Connect provider (an issuer in this process, a simulated browser and the desktop's " +
            "real loopback redirect) is refused until the home PC allows the identity it saw, and joins the same way; a Steam " +
            "assertion is confirmed by the host; the home PC shares the host with a friend (that identity allowed as a friend): the " +
            "friend's computer signs in and lists the host's engines, every other route refuses it (access.friend) and it never " +
            "joins the network, a sign-in under HOME-PC's ID is refused (signin.device_taken), and stopping sharing revokes it at " +
            "once with no network removal; the home PC makes a share link for all of its hosts (each host keeps only its verifier), a " +
            "friend's computer pastes it, is refused with a wrong secret, signs in as a friend on the host the link names, asks that " +
            "host which hosts the link covers and signs in to the other one, and stopping the link revokes it on both; a non-member can't change sign-in and removing the owner account revokes the " +
            "laptop. Reports " +
            "each step; loopback only, writes nothing to disk or the credential vault.", new { },
            async (_, cancellation) => await NodeLinkCheckAsync(cancellation, "signin")),
        Tool("nearby_status", "Read whether this PC lets Martlet on the owner's other computers find it and ask to use its hosts " +
            "(on by default, \"off\" only after the owner turned it off) and which paired hosts it could share from hosts.json (hosts " +
            "it runs or reaches over SSH; this PC's own host service set up from the host dashboard is found from Docker by the " +
            "desktop, not here). Read-only; contacts nothing and returns no addresses, SSH targets or keys.", new
        {
            dataDirectory = new { type = "string" }
        },
            arguments => NearbyStatus(arguments)),
        Tool("pc_scope", "Read the PC scope (docs/ACCOUNTS.md) the desktop with this data folder uses: which folder holds what this PC " +
            "is for and its host service (machine-wide %ProgramData%\\Martlet for the default data folder, the data folder itself " +
            "for any other, or MARTLET_PC_DIRECTORY), whether every Windows user of this PC can change it, the role (companion or " +
            "host) and where it is read from (the PC folder, or this Windows user's earlier choice that moves there at the next " +
            "start), whether this Windows user has a choice of their own, the host service's remembered roles and whether this or " +
            "another Windows user's Docker Desktop runs them. Read-only; returns no paths, user names or SIDs.", new
        {
            dataDirectory = new { type = "string" }
        },
            arguments => PcScopeStatus(arguments)),
        Tool("virtualization_status", "Read whether Windows is ready for Docker Desktop's WSL 2 engine (virtualization in the firmware, " +
            "the Windows hypervisor, Virtual Machine Platform, Windows Subsystem for Linux, their host services, WSL version and status), " +
            "pending and required restarts, blockers and recovery guidance, whether Docker Desktop is installed and running, its engine " +
            "state and the Windows check its engine last failed when it started (\"Virtualization support not detected\"), and any setup " +
            "Martlet continues after a Windows restart. Read-only; starts no VM, changes nothing and returns no distribution names.", new
        {
            dataDirectory = new { type = "string" }
        },
            async (arguments, cancellation) => await VirtualizationStatusAsync(arguments, cancellation)),
        Tool("host_service_status", "Read this PC's own Martlet host service on Docker Desktop the way the host dashboard does: its stage " +
            "(DockerMissing, DockerNotRunning, NotSetUp, Stopped, Running), Martlet version, host ID, whether its published address is " +
            "still this PC's and answers, the installed roles, the Martlet network it joined (state and the desktops paired " +
            "with it) and sharedGpu, the warning Martlet shows when its voice engine shares the graphics card with other roles. Reads " +
            "Docker and the gateway's nonsecret files only (never its agent token, keys or pairings); returns no addresses and " +
            "changes nothing.", new { },
            async (_, cancellation) => await HostServiceStatusAsync(cancellation)),
        Tool("node_link_check", "Run commands between Martlet computers end to end on this PC's loopback: the real gateway (pinned TLS, " +
            "pairing, signed requests, the command mailbox and its storage), the desktop's real client and agent loop with a fixture " +
            "runner, two fixture devices. Checks that only known commands are accepted, only the host's agent (local token) takes them, " +
            "output and outcomes reach the sender, secrets never appear in lists or saved copies, cancel works, commands run side by " +
            "side (a second change to one role waits for the first; an update waits for what runs and holds what was sent after it; " +
            "an older agent still gets one at a time), an update that waits holds the queue and the sender sees what its command " +
            "waits behind, and commands survive a restart. Contacts nothing " +
            "outside loopback and touches no real credentials, Docker or installs.", new { },
            async (_, cancellation) => await NodeLinkCheckAsync(cancellation)),
        Tool("host_engine_check", "Check that a Martlet host runs changes side by side and makes only colliding ones wait: runs " +
            "this checkout's real martlet-host engine in one disposable ubuntu:24.04 container (no network, never pulled, removed " +
            "afterwards; Martlet's own host containers and volumes are never touched) against a fixture setup. A change holds and " +
            "records its locks; read-only commands still run; status names the holders; an automatic run (no terminal, no --yes) " +
            "stops at once with exit 75 and MARTLET-BUSY, changing nothing; an attended run waits and gives up after " +
            "MARTLET_LOCK_WAIT; adds of different roles run side by side while the same role or exclusive group waits; setup and " +
            "update wait for every change and hold back later ones; a waiting run continues when the holder is killed, and so does " +
            "a run without a terminal or --yes told to wait (Update hosts now); no stale lock or record remains; the journal " +
            "records it; the desktop's reader reads the busy line. With a " +
            "fake docker CLI it also checks the Docker method: setup does not replace the network holder while an engine session " +
            "(an add) runs in it (an automatic setup stops with MARTLET-BUSY, an attended one waits, then replaces it), an engine " +
            "left in a replaced holder's namespace stops at once, any command starts a holder that failed to start after a restart, " +
            "update adds the keeper (martlet-host-keeper) once, and keep starts a holder that isn't running (with or without " +
            "Docker's start error) and leaves a running one alone. A fixture role with two variants (like the stt role's whisper and " +
            "Parakeet engines) shows describe listing each variant's own choices, suggestion and GPU option with their condition, an " +
            "add keeping a variant's own choices, GPU option and prepare step, and a switch stopping the previous variant only once " +
            "the chosen one is prepared (also when a failed switch is run again). The real voice-recognition role's files show its " +
            "GPU or CPU choice on an x86_64 host with a card and its processor build on ARM64. Returns notRun when Docker or the " +
            "image is missing.", new { },
            async (_, cancellation) => await HostEngineCheck.RunAsync(cancellation)),
        Tool("host_supply_check", "Check that Martlet sets up a native Ubuntu host without internet access by sending what it needs " +
            "from this PC: with the production HostSupplier and HostCheckout and this checkout's real martlet-host, in one disposable " +
            "ubuntu:24.04 container on an internal Docker network (a private LAN address, no way out; never pulled, removed " +
            "afterwards). This PC downloads the .NET SDK and the gateway's NuGet packages for real (about 240 MB the first time, cached " +
            "in cacheDirectory); the online command stops plainly (no git, no internet) instead of running a missing engine; the files " +
            "arrive intact over a tar stream; setup builds the gateway, creates its identity and starts it healthy without internet; " +
            "a second pass sends nothing and removes the installed SDK's archive; update rebuilds offline. FIXTURE systemctl and ip " +
            "stand in for systemd and iproute2. Returns notRun when Docker or the image is missing.", new
        {
            cacheDirectory = new { type = "string", maxLength = 260 }
        },
            async (arguments, cancellation) => await HostSupplyCheck.RunAsync(arguments, cancellation)),
        Tool("host_docker_shell_check", "Check the shell Martlet runs on a Docker host over SSH on a computer shaped like a NAS " +
            "(TerraMaster, Synology, QNAP): the production HostCheckout text, as the desktop's Docker method joins it, in one disposable " +
            "ubuntu:24.04 container (no network, never pulled, removed afterwards) where Docker is only on the login shell's PATH and " +
            "there is no git. Checks that an SSH command's plain shell finds no Docker but HostCheckout.DockerPath does; that without " +
            "git the host image is built from GitHub's source archive (main's when the release tag has none), unpacked into a " +
            "temporary folder that is removed afterwards, and then the engine runs; that an image already there builds nothing; and " +
            "that a failed download builds nothing and runs no engine. It also runs HostCheckout.NasProbe: a plain Linux is no NAS, and " +
            "the marker of TerraMaster, Synology, QNAP, Unraid and TrueNAS each names that NAS (Martlet sets up a NAS only in Docker). " +
            "FIXTURE docker and curl stand in for Docker and GitHub. " +
            "Returns notRun when Docker or the image is missing.", new { },
            async (_, cancellation) => await HostDockerShellCheck.RunAsync(cancellation)),
        Tool("host_update_check", "Rehearse how Martlet coordinates its own host service updates, with the desktop's production " +
            "update tracker and busy reader: an Update host run window claims its host so the automatic pass leaves it to that run " +
            "(no second engine run that finds the host locked by Martlet's own update and reports it busy, for its pairing or as this " +
            "PC's own host service); overlapping routes end separately; an update started elsewhere is named as another update; a " +
            "host found current stops waiting and its stale note says it is updated; Update hosts now waits for another change; this " +
            "PC's own host service follows the app's version after an update by itself (in the background, giving way to another " +
            "route, this PC's own pending update and the conversation, retried when busy, settled once current). Pure logic: contacts " +
            "nothing and touches no Docker, host or data directory.", new { },
            _ => HostUpdateCheck.Run()),
        Tool("app_update_check", "Rehearse how Martlet installs its own update end to end with the desktop's production update " +
            "helper (the same script and hidden start) in a disposable folder: a stand-in for Martlet that exits, and a FIXTURE " +
            "standing in for the installer and the restarted Martlet. For an install another computer asked for and an automatic " +
            "one (from the notification area, whose installer fails), the helper waits for Martlet to exit, runs the installer with " +
            "no window at all (/VERYSILENT, no questions, no restart), records its exit code, logs every step in update.log and " +
            "starts Martlet again minimized (in the notification area when it was there); one you confirmed shows the installer's " +
            "progress window (/SILENT). Updates installed beside Martlet switch folders in seconds, and fall back to the whole " +
            "installer when the program folder is in use (a file held open, or a program Martlet started still working in it), " +
            "but not when that program works outside it. Also checks the note Martlet leaves itself so the restarted Martlet shows the character and " +
            "listens again (read once, only what was on, ignored when stale). Installs nothing, starts no real Martlet and contacts " +
            "nothing.", new { },
            async (_, cancellation) => await AppUpdateCheck.RunAsync(NodeLinkCheckProgram(), cancellation)),
        Tool("api_selftest", "Rehearse API keys for software outside the Martlet network end to end with the production code: two " +
            "real gateways on 127.0.0.1 (pinned TLS, the real Ollama relay route over a fixture Ollama, NOT AI), a simulated desktop " +
            "that creates, syncs and revokes keys through its paired client, and a plain HTTPS client sending Authorization: Bearer. " +
            "Checks scopes (read, voice, manage), refusals (no key, wrong key, endpoints keys may never use), sync to a second host and " +
            "a restart, last-used reports, revocation mid-reply, stale copies and expiry. Loopback only; writes nothing to disk or the " +
            "credential vault.", new { },
            async (_, cancellation) => await NodeLinkCheckAsync(cancellation, "api")),
        Tool("openai_selftest", "Rehearse the OpenAI-compatible endpoint (docs/OPENAI_ENDPOINT.md) end to end with the production code: " +
            "two real gateways on 127.0.0.1 (pinned TLS, the real Ollama relay over a fixture Ollama each, NOT AI) joined in one Martlet " +
            "network by a simulated desktop's real pairing and network sync; keys made with the Share page's scopes (openai, " +
            "equal-priority) and synced to both hosts; a plain HTTPS client with Authorization: Bearer at the front door. Checks " +
            "GET /v1/models listing both hosts' models, the desktop's signed model read, a chat run on the front door with the owner's " +
            "context size and the model kept loaded, a streamed chat sent on to the other host, refusals in OpenAI's error form, the " +
            "owner's reply stopping a shared request, and revocation on both hosts. Loopback only; writes nothing to disk or the " +
            "credential vault.", new { },
            async (_, cancellation) => await NodeLinkCheckAsync(cancellation, "openai")),
        Tool("front_door_selftest", "Rehearse a host's front door (docs/OPENAI_ENDPOINT.md), its public listener on its own port, end " +
            "to end with the production code: two real gateways on 127.0.0.1 (pinned TLS, the real Ollama relay over a fixture Ollama " +
            "each, NOT AI) joined in one Martlet network by a simulated desktop; the first host's front door open on a free port; " +
            "plain HTTPS clients on that port play a browser and an app behind a proxy. Checks the security audit naming the front " +
            "door, the public page (Online and sign-in only: no model, host or address, no script, a strict CSP), 404 for every " +
            "private path, 401 missing_api_key without a key (what Check address looks for), both hosts' models and a streamed chat " +
            "sent on to the other host, no lockout after wrong keys, sign-in refusing a forged form, another site and a wrong key, the " +
            "signed-in page listing the models, and revocation ending the session. Loopback only; writes nothing to disk.", new { },
            async (_, cancellation) => await NodeLinkCheckAsync(cancellation, "front-door")),
        Tool("proxy_manager_check", "Check Share › Show setup › Set it up in Nginx Proxy Manager with the production ProxyManager " +
            "against a FIXTURE Nginx Proxy Manager API in memory (contacts nothing): it finds NPM, refuses a wrong password, plans to " +
            "change the site that already has the name (not add a second one) and keeps its certificate, changes where it sends people " +
            "and the settings streaming needs, changes nothing when it is set up already, adds a site with a wildcard certificate that " +
            "covers the name, asks Let's Encrypt when only an expired certificate covers it, and sends the password only to sign in.",
            new { }, async (_, cancellation) => await ProxyManagerCheck.RunAsync(cancellation)),        Tool("outside_path_check", "Reach a host from outside home on real sockets: builds this checkout's Linux gateway in the .NET SDK " +
            "image and runs it (owner-init, owner-exposure, owner-pair, serve) in disposable aspnet containers on a Docker network " +
            "numbered from TEST-NET-3, its port published on 127.0.0.1, so every connection reaches it from 203.0.113.1 (an outside " +
            "source). The host's home address answers nothing; the desktop's real pairing client, connection, network sync and " +
            "HostRoutes must fall back to the outside address. Checks typed codes refused and a device card allowed from outside, " +
            "the roster signing the advertised address, home-fail then outside-succeed, the guard locking out a stranger and the audit " +
            "naming the outside source. Never pulls; removes its containers, volumes and network (keeps a NuGet cache volume).", new { },
            async (_, cancellation) => await OutsidePathCheck.RunAsync(cancellation)),
        Tool("mac_host_check", "Check the Mac host as far as this PC can: publishes this checkout's gateway self-contained for osx-arm64 " +
            "and osx-x64 in the .NET SDK image (the files the Mac app bundles), runs its macos-setup help and checks macos-status " +
            "refuses off a Mac, and checks the platform catalog for a Mac host's machine report (Ollama and whisper allowed, F5 and " +
            "Audio2Face refused for want of an NVIDIA GPU). Running on a Mac is reported NOT RUN. Never pulls.", new { },
            async (_, cancellation) => await MacHostCheck.RunAsync(cancellation)),
        Tool("exposure_selftest", "Rehearse a host reachable from outside home end to end with the production code: one real gateway " +
            "on 127.0.0.1 (pinned TLS) told to treat every connection as outside home, a desktop paired at home through its paired " +
            "client, and a stranger's pinned HTTPS client. Checks that pairing from outside is refused until the owner allows it, " +
            "guessing is locked out with a doubling Retry-After, routes anyone may call have a per-address budget, and the paired " +
            "desktop reads the security audit (/martlet/v1/security/audit) and the host log lines naming each source. When this PC has " +
            "a Tailscale address, a second gateway on this PC's home address gets an outside address and is reached through this " +
            "PC's own Tailscale address (refused as outside home until sign-in is set up, then answering; it stops listening there " +
            "when the outside address is removed); otherwise that step is reported in notRun. Writes nothing to disk.", new { },
            async (_, cancellation) => await NodeLinkCheckAsync(cancellation, "exposure")),
        Tool("host_move_selftest", "Rehearse a host whose computer got another address on the home network (a restart or a new DHCP " +
            "lease) end to end with the production code: a real gateway (pinned TLS) on 127.0.0.3 whose certificate also names the " +
            "address it was paired at (127.0.0.2, where nothing answers now), its real find answer (UDP, HostFind) and a desktop's " +
            "real paired connection (HostRoutes). Checks that the host answers a find with its host ID only (and not Nearby's find), " +
            "the desktop finds it at its new address by itself with the same key and pairing and raises the move it keeps with the " +
            "pairing, dials it there at once next time, the pinned-key probe reaches it there, a computer with another key that " +
            "answers for the same host ID is never used, and a host back at its paired address is found there again. Loopback only " +
            "(finds never reach the home network); writes nothing.", new { },
            async (_, cancellation) => await NodeLinkCheckAsync(cancellation, "move")),
        Tool("hosts_find", "Ask this PC's home network where Martlet hosts are, as Martlet does when a paired host doesn't answer at " +
            "its saved address: one find on UDP 9444 to each private subnet's broadcast address, the limited broadcast and loopback " +
            "(never a subnet scan), answers for `seconds` (default 2, at most 5). Lists each host ID that answered and the private " +
            "address it answered from. With dataDirectory it also says whether that host is paired there (hosts.json) and whether " +
            "it answered at its paired address, at the address saved as where it moved to, or somewhere new (Martlet then checks its " +
            "pinned key there before it uses it). Sends no keys or credentials and changes nothing.", new
        {
            dataDirectory = new { type = "string" },
            seconds = new { type = "number" }
        },
            async (arguments, cancellation) => await HostsFindAsync(arguments, cancellation)),
        Tool("gpu_priority_status", "Read GPU priority (live turn first) on every Martlet host paired in a desktop data directory " +
            "(hosts.json), through each host's own gateway (pinned TLS; the pairing secret from Windows Credential Manager only signs " +
            "the request, never returned): GET /martlet/v1/priority with each route's lane (live or pool) and graphics cards, each " +
            "card's hold state (held, live and pool requests running, holds), whether the whole host is held, the holds, the preempted " +
            "and refused counts, the last preemptions and refusals (what held the card) and placement warnings. A host older than " +
            "GPU priority is reported as such. Read-only: takes no hold and starts no work; contacts only paired hosts, never a host a " +
            "friend shares with that desktop (it keeps its priority to its owner).", new
        {
            dataDirectory = new { type = "string" }
        },
            async (arguments, cancellation) => await GpuPriorityStatusAsync(arguments, cancellation)),
        Tool("gpu_priority_selftest", "Rehearse GPU priority (live turn first) end to end with the production code: real gateways on " +
            "127.0.0.1 (pinned TLS) with Thinking's Ollama relay (lane live) and the Deep thinking role's (lane pool) placed on graphics " +
            "cards as martlet-host places them, each over its own fixture Ollama server on loopback (NOT AI, no GPU used), a simulated " +
            "desktop with the desktop's paired client and its hold client (HostLiveGpuHold). Checks the host's GPU map and its warning " +
            "to pin each Ollama server to its own GPU, that a live reply stops a running think on the same card at once (job.preempted, " +
            "the Ollama request aborted), that a think is turned away while a live reply or a hold keeps the card (job.busy), holds " +
            "(renew, release, a 1 s hold that ends on its own, a hold that stops running work), that live replies never wait, that a " +
            "think on its own card runs on, and that an older host without holds leaves the live turn alone. Returns each step and the " +
            "host's GET /martlet/v1/priority report (per-card hold state, holds, last preemptions and refusals, warnings). Loopback " +
            "only; writes nothing to disk or the credential vault.", new { },
            async (_, cancellation) => await NodeLinkCheckAsync(cancellation, "gpu-priority")),
        Tool("household_sharing", "Read what each account of a household shares (docs/ACCOUNTS.md \"Sharing\") from a desktop's data " +
            "directory: its copy of the household settings (shared-settings.json, the sharing.<account> entries) and the account in use " +
            "(accounts\\session.json). Per account: its ID (32 hex), how many characters it shares as a copy and together, each shared " +
            "character's key (first 8 hex digits), mode, memory space when shared together, lorebook and entry counts and whether it sets " +
            "a look and a voice, the characters shared together it talks to (and whether their owner still shares them), whether new " +
            "facts about this person go to the household space, the entry's size, writer and time; plus how many household characters " +
            "the account in use sees and entries that can't be read. Never a name, a personality's text or a lorebook entry. Read-only; " +
            "contacts nothing.", new
        {
            dataDirectory = new { type = "string" }
        },
            arguments => HouseholdSharingStatus.Read(DataDirectory(arguments))),
        Tool("accounts_sync_selftest", "Rehearse the household's account directory (docs/ACCOUNTS.md) end to end with the production " +
            "code: two real gateways on 127.0.0.1 (pinned TLS, signed requests, in-memory network.json and accounts.json) bound to one lab " +
            "network, two member desktops and one outside computer with real network keys and the desktop's paired client " +
            "(HostAccounts.cs). Walks binding both hosts, migrating to the owner account (the ID derived from the network ID, created by " +
            "the founder), a second desktop checking signatures against its roster, a new person synced through one host only, offline " +
            "renames (the later wins), a removal that an offline edit can't undo, an outside computer's entry and a forged change refused " +
            "(rejected count), a restarted host keeping accounts.json, digests matching on every copy and an unsigned request refused. " +
            "Synthetic accounts only; loopback only.", new { },
            async (_, cancellation) => await NodeLinkCheckAsync(cancellation, "accounts")),
        Tool("work_sharing_status", "Devices > Sharing work from a data directory: the choices (work-sharing.json, the work-sharing " +
            "shared setting: for Speaking, Thinking, Listening and Deep thinking whether it is shared when its computer is busy, " +
            "the order chosen (this-pc being each companion PC's own host service) and the computers never used; which computers " +
            "are kept for one companion PC), the paired computers the shared plan says run each job's engine, the computer each " +
            "job uses now, and the order this PC (or deviceId) tries them in with the production planner (WorkSharing.Order). " +
            "Also the pool lists (pools.json, the pools shared setting, and pools-local.json): for each area its members in order " +
            "(key, kind, name, on, kept for, nonsecret settings, whether this PC may use it, whether a cloud member is agreed to), " +
            "whether it is off or uses its fallback, and for Speaking, Listening and Thinking the computers this PC tries " +
            "(PoolRouting.Hosts). Host and device IDs, provider and model names only; never a key. Read-only.", new
        {
            dataDirectory = new { type = "string" },
            deviceId = new { type = "string" }
        },
            async (arguments, cancellation) => await WorkSharingCheck.StatusAsync(DataDirectory(arguments), OptionalString(arguments, "deviceId"), cancellation)),
        Tool("external_endpoints_status", "Devices > Cloud providers and servers from a data directory, with the production " +
            "ExternalEndpoints: every endpoint outside this PC and the paired computers that a saved choice sends work to (the " +
            "Thinking, Listening and Speaking routes and Thinking's fallback in settings.json, external Thinking pool members in " +
            "thinking-pool.json, address and cloud members of every pool list in pools.json and pools-local.json, and HTTP MCP " +
            "servers in mcp.json), each once with its name, kind (cloud or server on your network), scheme-host-port address and its " +
            "uses (purpose, page, model or MCP server name, on). Names, addresses without paths, models and page names only; never " +
            "a key. Read-only.", new
        {
            dataDirectory = new { type = "string" }
        },
            async (arguments, cancellation) => await ExternalEndpointsCheck.StatusAsync(DataDirectory(arguments), cancellation)),
        Tool("work_sharing_check", "Rehearse Sharing work with the production planner (WorkSharing.Order) and queue (WorkQueue) on " +
            "the four-computer example (three companion PCs with host services, machine 2 without a voice, machine 4 for lip-sync " +
            "and pictures) with simulated computers that run one request at a time and turn another away at once, as a host's " +
            "gateway does (job.busy), NOT real hosts or models: each companion's order with its own computer first, Thinking left " +
            "unshared, a busy voice passed over at once, machine 2 waiting while both voices are busy and taken by whichever frees " +
            "first, four segments at once spread over both, a computer kept for one companion PC or unticked for a job left out, " +
            "an unanswering computer skipped, Deep thinking leaving out a kept computer, and the shared setting's round trip. " +
            "Then pools: Sharing work's choices moved into the Speaking list, the list's order per companion PC, a member turned " +
            "off, Thinking's own model first, an empty list off for Speaking and voice loudness for lip-sync, a rate-limited cloud " +
            "member passed over by its member key, a cloud member in the list taking a segment only when the computer before it is " +
            "busy, and the pools shared setting's round trip. In-process; reads nothing.", new { },
            async (_, cancellation) => await WorkSharingCheck.RunAsync(cancellation)),
        Tool("network_recommendation_check", "Rehearse Home's Recommended setup for all your computers with the production network " +
            "recommender (NetworkRecommender) on built-in fixture networks, NOT real computers: two companion PCs and two hosts with " +
            "nothing set up, a host with two NVIDIA cards, a Windows host whose voice shares its card, a crowded network, Deep " +
            "thinking beside the voice, a companion PC with Singing whose hosts are gone (no key, then a saved free key), heavy " +
            "roles on a companion PC, Thinking with only a processor host, hosted Thinking the owner " +
            "chose, a host left out of the Thinking pool, the voice host not answering (just now, 4 and 25 minutes), the owner's " +
            "recommendation preferences (reply quality, online services only as a backup or Yes, the host card share), and the applied recommendation. Each " +
            "step names its rule (1-12, or preferences), passed and the change list, target roles, jobs, pools and notes. In-process; reads nothing.", new { },
            _ => NetworkRecommendationCheck.Run()),
        Tool("node_presence_status", "When your other computers go away or come back, from a data directory: the per-PC away time " +
            "(node-presence.txt; Settings > Your other computers, default 10 minutes), the rules (missing after 30 seconds without " +
            "an answer, back after 30 seconds of answers, the back notice shown 10 minutes) and the report the desktop writes when " +
            "a computer's state changes (node-presence.json: each paired computer's state Answering, NotAnswering, Missing, Away, " +
            "Returning or Back, since when, and the notices Home shows, such as \"Working with less: gpu-box isn't answering\"). " +
            "Host IDs, computer names and times only. Read-only.", new
        {
            dataDirectory = new { type = "string" }
        },
            arguments => NodePresenceCheck.Status(DataDirectory(arguments))),
        Tool("node_presence_check", "Rehearse the presence notices and events with the production rules (PresenceWatch, " +
            "NodePresenceNotices, NodePresenceSettings, NodePresenceReport) on scripted timelines: a check every 15 seconds with " +
            "the desktop's presence rule and a 5-second tick, NOT real hosts. One flaky miss says nothing; 30 seconds without an " +
            "answer goes missing once with a notice that names the failover move, the job that waits and the pools; still missing " +
            "after 10 minutes stays away once; 30 seconds of answers comes back once and the notice clears after 10 minutes or " +
            "when dismissed; a flapping computer stays one absence; the away time follows the per-PC choice; the report round trip; " +
            "an unpaired computer is forgotten. In-process; writes only a temporary folder.", new { },
            _ => NodePresenceCheck.Run())
    ];

    /// <summary>Martlet.NodeLinkCheck's executable in this source checkout's build (the same configuration as this server).</summary>
    private static System.Diagnostics.Process? signInLab;

    private static System.Diagnostics.Process? roleLab;

    /// <summary>Starts a live lab (Martlet.NodeLinkCheck <paramref name="mode"/> &lt;data directory&gt;) and returns the process and
    /// its first line, which says whether it is ready. The lab keeps its pairing secrets in the lab credential folder.</summary>
    private static async Task<(System.Diagnostics.Process Process, JsonElement Ready)> StartLabAsync(string mode, string directory, string name,
        CancellationToken cancellation, params string[] extra)
    {
        if (Environment.GetEnvironmentVariable(Martlet.Credentials.Windows.LabCredentialNative.Variable) is not { Length: > 0 })
            throw new InvalidOperationException("Run with Invoke-MartletMcp.ps1 -LabCredentials: the lab keeps its pairing secret in a lab folder, never Windows Credential Manager.");
        Directory.CreateDirectory(directory);
        var start = new System.Diagnostics.ProcessStartInfo(NodeLinkCheckProgram())
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(directory);
        foreach (var argument in extra) start.ArgumentList.Add(argument);
        var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException($"Couldn't start the {name}.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
        if (line is null || !line.Contains("\"ready\":true", StringComparison.Ordinal))
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"The {name} didn't start: " + (line ?? await process.StandardError.ReadToEndAsync(cancellation)));
        }
        return (process, JsonSerializer.Deserialize<JsonElement>(line));
    }

    /// <summary>Ends a live lab: closing its standard input stops it.</summary>
    private static void StopLab(System.Diagnostics.Process? lab)
    {
        if (lab is not { HasExited: false } running) return;
        running.StandardInput.Close();
        if (!running.WaitForExit(10_000)) running.Kill(entireProcessTree: true);
    }

    /// <summary>Starts, reads, asks or stops the live lab for switching computers between companion and host PC
    /// (Martlet.NodeLinkCheck role-lab) for a disposable data directory.</summary>
    private static async Task<object> RoleLabAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var directory = DataDirectory(arguments);
        var status = Path.Combine(directory, "role-lab.json");
        switch (OptionalString(arguments, "action"))
        {
            case "start":
            {
                if (roleLab is { HasExited: false }) throw new InvalidOperationException("The role lab is already running; stop it first.");
                var (process, ready) = await StartLabAsync("role-lab", directory, "role lab", cancellation);
                roleLab = process;
                return ready;
            }
            case "status":
                return await ReadLabStatusAsync(status, roleLab, cancellation);
            case "ask":
            {
                if (roleLab is not { HasExited: false }) throw new InvalidOperationException("Start the role lab first.");
                var role = OptionalString(arguments, "role");
                if (role is not ("host" or "companion")) throw new ArgumentException("role is host or companion.");
                var ask = Path.Combine(directory, "role-lab.ask");
                await File.WriteAllTextAsync(ask + ".tmp", role, cancellation);
                File.Move(ask + ".tmp", ask, overwrite: true);
                return new { asked = role };
            }
            case "stop":
                StopLab(roleLab);
                roleLab = null;
                return new { stopped = true };
            default:
                throw new InvalidOperationException("action is start, status, ask or stop.");
        }
    }

    /// <summary>A live lab's status file, read again when the lab is swapping it in at that moment; whether the lab still runs
    /// when there is none yet.</summary>
    private static async Task<object> ReadLabStatusAsync(string path, System.Diagnostics.Process? lab, CancellationToken cancellation)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return File.Exists(path) ? JsonSerializer.Deserialize<JsonElement>(await File.ReadAllBytesAsync(path, cancellation))
                    : new { ready = false, running = lab is { HasExited: false } };
            }
            catch (IOException) when (attempt < 5) { await Task.Delay(50, cancellation); }
        }
    }

    /// <summary>Starts, reads or stops the live sign-in lab (Martlet.NodeLinkCheck signin-lab) for a disposable data directory.</summary>
    private static async Task<object> SignInLabAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var directory = DataDirectory(arguments);
        var status = Path.Combine(directory, "signin-lab.json");
        switch (OptionalString(arguments, "action"))
        {
            case "start":
            {
                if (signInLab is { HasExited: false }) throw new InvalidOperationException("The sign-in lab is already running; stop it first.");
                var mode = OptionalString(arguments, "mode") ?? "owner";
                if (mode is not ("owner" or "friend" or "account")) throw new ArgumentException("mode is owner, friend or account.");
                var extra = new List<string> { "--mode", mode };
                if (OptionalBool(arguments, "signInDesktop") == true)
                    extra.Add(mode == "friend" ? "--sign-in-desktop" : throw new ArgumentException("signInDesktop goes with mode friend."));
                if (OptionalBool(arguments, "shareWithFriend") == true)
                    extra.Add(mode == "owner" ? "--share-with-friend" : throw new ArgumentException("shareWithFriend goes with mode owner."));
                if (OptionalBool(arguments, "household") == true)
                    extra.Add(mode == "account" ? "--household" : throw new ArgumentException("household goes with mode account."));
                if (OptionalBool(arguments, "joinDesktop") == true)
                    extra.Add(mode == "account" && !extra.Contains("--household") ? "--join-desktop"
                        : throw new ArgumentException("joinDesktop goes with mode account, without household."));
                var (process, ready) = await StartLabAsync("signin-lab", directory, "sign-in lab", cancellation, [.. extra]);
                signInLab = process;
                return ready;
            }
            case "status":
                return await ReadLabStatusAsync(status, signInLab, cancellation);
            case "stop":
                StopLab(signInLab);
                signInLab = null;
                return new { stopped = true };
            default:
                throw new InvalidOperationException("action is start, status or stop.");
        }
    }

    /// <summary>The PC scope the desktop with this data folder uses (<see cref="PcFolder"/>): where what this PC is for and its
    /// host service are kept, who may change them and what they say. Read-only: the move of an earlier per-user choice to the
    /// PC folder happens when the desktop starts, so this says when it is still to come. No paths, user names or SIDs.</summary>
    private static object PcScopeStatus(JsonElement arguments)
    {
        var directory = DataDirectory(arguments);
        var pc = PcFolder.For(directory);
        var separate = pc.Separate(directory);
        static string? Role(string folder)
        {
            try
            {
                var path = Path.Combine(folder, "device-role.txt");
                if (!File.Exists(path)) return null;
                return File.ReadAllText(path).Trim() == "Host" ? "host" : "companion";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return "unreadable"; }
        }
        var shared = Role(pc.Directory);
        var mine = separate ? Role(directory) : shared;
        var roles = ThisPcHostRoles.Load(pc.Directory);
        var pending = roles is null && separate ? ThisPcHostRoles.Load(directory) : null;
        var user = ThisPcHostRoles.WindowsUser(pc.Directory);
        string? me = null;
        if (OperatingSystem.IsWindows())
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            me = identity.User?.Value;
        }
        return new
        {
            folder = pc.Kind switch { PcFolderKind.MachineWide => "machine-wide", PcFolderKind.Override => "override", _ => "data-folder" },
            where = pc.Where,
            sharedByWindowsUsers = pc.Shared,
            folderExists = Directory.Exists(pc.Directory),
            everyWindowsUserCanChange = pc.Shared && OperatingSystem.IsWindows() ? PcFolder.EveryUserCanChange(pc.Directory) : null,
            role = shared ?? mine,
            roleReadFrom = shared is not null ? (separate ? "pc-folder" : "data-folder")
                : mine is not null ? "this-windows-user (moves to the PC folder when the desktop starts)" : null,
            chosenByThisWindowsUser = mine is not null,
            hostServiceRoles = roles ?? pending,
            hostServiceRolesReadFrom = roles is not null ? (separate ? "pc-folder" : "data-folder")
                : pending is not null ? "this-windows-user (moves to the PC folder when the desktop starts)" : null,
            hostServiceSeenBy = roles is null ? null : user is null ? "unknown" : user == me ? "this-windows-user" : "another-windows-user"
        };
    }

    /// <summary>Whether Windows can run Docker Desktop (the desktop's WindowsVirtualizationSetup reads the same facts), whether
    /// Docker Desktop is installed and running, and the setup Martlet continues after a restart. Never returns paths.</summary>
    private static async Task<object> VirtualizationStatusAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var directory = DataDirectory(arguments);
        var failedStartCheck = await DockerDesktopStatus.ReadPreconditionAsync(cancellation);
        var state = await WindowsVirtualization.ProbeAsync(cancellation) with { DockerPrecondition = failedStartCheck };
        var note = ContinueSetup.Read(directory, DateTimeOffset.Now);
        bool startsAtSignIn;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\RunOnce");
            startsAtSignIn = key?.GetValue("MartletContinueSetup") is string;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            startsAtSignIn = false;
        }
        static bool Running(string name)
        {
            var processes = System.Diagnostics.Process.GetProcessesByName(name);
            try { return processes.Length > 0; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        return new
        {
            ready = state.Ready,
            blocked = state.Blocked,
            firmwareOff = state.FirmwareOff,
            needsWindowsChanges = state.NeedsChanges,
            restartPending = state.RestartPending,
            restartRequired = state.RestartRequired,
            problems = state.Problems(),
            recovery = state.Recovery,
            probeIssues = state.ProbeIssues,
            firmware = state.Firmware,
            hypervisor = state.Hypervisor,
            virtualMachinePlatform = state.MachinePlatform.ToString(),
            windowsSubsystemForLinux = state.Subsystem.ToString(),
            wsl = state.Wsl,
            wslStatus = state.WslStatus.ToString(),
            wslStatusExitCode = state.WslStatusExitCode,
            hostComputeService = state.ComputeService.ToString(),
            hostNetworkService = state.NetworkService.ToString(),
            virtualMachine = state.VirtualMachine,
            summary = state.Describe(),
            dockerDesktop = new
            {
                installed = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Docker", "Docker", "Docker Desktop.exe")),
                running = Running("com.docker.backend") || Running("Docker Desktop"),
                engine = await DockerDesktopStatus.ReadAsync(cancellation),
                failedStartCheck,
                windowsCanFixFailedStartCheck = WindowsVirtualization.WindowsCanFix(failedStartCheck),
                failedStartCheckNeedsWindowsChanges = state.DockerNeedsWindows
            },
            continueSetup = new
            {
                pending = note is not null,
                kind = note?.Kind.ToString(),
                task = note?.Task,
                created = note?.Created,
                startsAtSignIn
            }
        };
    }

    /// <summary>gpu_priority_status: GPU priority (live turn first) on every host paired in the data directory, through
    /// Martlet.NodeLinkCheck gpu-priority-status, which signs with the pairing secret from Windows Credential Manager and returns
    /// no secret. Nothing is contacted when the data directory pairs no host.</summary>
    private static async Task<object> GpuPriorityStatusAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var directory = DataDirectory(arguments);
        if (!File.Exists(Path.Combine(directory, "hosts.json")))
            return new { dataDirectory = directory, hosts = Array.Empty<object>(), note = "No host is paired in this data directory (no hosts.json)." };
        return await NodeLinkCheckAsync(TimeSpan.FromMinutes(2), cancellation, ["gpu-priority-status", directory]);
    }

    /// <summary>This PC's own host service as the desktop's host dashboard reads it (<see cref="LocalHostService"/>). The
    /// published address itself is not returned, only whether it is still this PC's and answers.</summary>
    private static async Task<object> HostServiceStatusAsync(CancellationToken cancellation)
    {
        var state = await LocalHostService.ProbeAsync(cancellation);
        return new
        {
            stage = state.Stage.ToString(), ready = state.Ready, version = state.Version, hostId = state.HostId,
            published = state.Address is not null, publishedEverywhere = state.PublishedEverywhere, addressOnThisPc = state.AddressOnThisPc, answering = state.Answering,
            roles = state.Roles, network = state.Network,
            desktops = state.Desktops.Select(d => new { id = d.Id, name = d.Name }).ToArray(),
            // The warning Companion › Voice and Devices show when this PC's voice engine shares the card with other roles.
            sharedGpu = SharedGpu.Warning("This PC", onWindows: true, SharedGpu.VoiceName(state.Roles), SharedGpu.Neighbours(state.Roles, false)),
            problem = state.Problem
        };
    }

    /// <summary>Shared "who does what" as the desktop keeps it in a data directory (the file names match Martlet.Desktop's
    /// ClusterSync): the sync choice, on unless cluster-sync.txt says "off", and cluster.json without host addresses.</summary>
    private static object ClusterStatus(JsonElement arguments)
    {
        var directory = DataDirectory(arguments);
        string? choice;
        try { choice = File.ReadAllText(Path.Combine(directory, "cluster-sync.txt")).Trim(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { choice = null; }
        object plan;
        var path = Path.Combine(directory, "cluster.json");
        if (!File.Exists(path)) plan = new { state = "none" };
        else
        {
            try
            {
                var copy = Martlet.Core.Cluster.ClusterPlan.Parse(File.ReadAllBytes(path));
                plan = new
                {
                    state = "loaded", revision = copy.Revision,
                    jobs = copy.Assignments.Select(a => new
                    {
                        job = a.Job, host = a.HostId, off = a.Off, failover = a.Failover, movedFrom = a.MovedFrom,
                        updatedBy = a.UpdatedBy, updatedAt = a.UpdatedAt
                    }).ToArray(),
                    hosts = copy.Nodes.Select(n => new { hostId = n.HostId, removed = n.Removed, roles = n.Roles.Select(r => r.Kind).ToArray() }).ToArray()
                };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException)
            {
                plan = new { state = "unreadable" };
            }
        }
        return new { sync = choice switch { "off" => "off", null => "on (default)", _ => "on" }, plan };
    }

    /// <summary>The Martlet network as the desktop keeps it in a data directory (network.json and network\device_ecdsa, the
    /// names Martlet.Desktop's NetworkIdentity uses), this data folder's device ID (device.json) and the Windows login's kind.
    /// No keys, signatures, addresses, e-mail or names of the Windows login are returned.</summary>
    private static object NetworkStatus(JsonElement arguments)
    {
        var directory = DataDirectory(arguments);
        var key = File.Exists(Path.Combine(directory, "network", "device_ecdsa"));
        var path = Path.Combine(directory, Martlet.Avatar.Audio2Face.Remote.NetworkLocalState.FileName);
        var pairedHosts = PairedHostsSummary(directory);
        var friends = FriendsSummary(directory);
        var shareLinks = ShareLinksSummary(directory);
        var routerPort = RouterPortSummary(directory);
        var householdSignIn = HouseholdSignInSummary(directory);
        var device = DeviceSummary(directory);
        var login = Martlet.Mcp.Shared.WindowsLogin.Current;
        var windowsLogin = new { kind = login.Kind, hasEmailHint = login.HasEmailHint, hasSid = login.Sid.Length > 0 };
        if (!File.Exists(path)) return new { state = "none", key, device, windowsLogin, pairedHosts, friends, shareLinks, routerPort, householdSignIn };
        Martlet.Avatar.Audio2Face.Remote.NetworkLocalState local;
        try { local = Martlet.Avatar.Audio2Face.Remote.NetworkLocalState.Parse(File.ReadAllBytes(path)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException)
        {
            return new { state = "unreadable", key, device, windowsLogin };
        }
        var roster = local.Roster;
        return new
        {
            state = roster is not null ? "member" : local.Waiting is not null ? "waiting" : "none",
            key,
            device,
            windowsLogin,
            networkId = roster?.NetworkId ?? local.Waiting?.NetworkId,
            revision = roster?.Revision,
            founder = roster?.Founder?.Id,
            waiting = local.Waiting is { } wait ? new { hostId = wait.HostId, checkNumber = wait.CheckNumber, since = wait.Since } : null,
            desktops = roster?.Members.Where(m => m.IsDesktop).Select(m => new { id = m.Id, name = m.Name, removed = m.Removed, updatedBy = m.UpdatedBy, changedAt = m.ChangedAt }).ToArray(),
            hosts = roster?.Members.Where(m => m.IsHost).Select(m => new { id = m.Id, name = m.Name, removed = m.Removed, updatedBy = m.UpdatedBy, changedAt = m.ChangedAt, outsideAddresses = m.Addresses?.Count ?? 0 }).ToArray(),
            adopt = local.Adopt,
            ignored = local.Ignored,
            removedFrom = local.RemovedFrom,
            pairedHosts,
            friends,
            shareLinks,
            routerPort,
            householdSignIn
        };
    }

    /// <summary>share-links.json in short (docs/NETWORK.md, "Sharing with a link"): the share links this PC made (the friend's
    /// name, whether it covers all hosts or which, when it was made) and the ones it accepted (who sent it, the hosts it signed in
    /// to and those you forgot), whether martlet:// links open Martlet for this Windows user, and whether another start left a
    /// clicked link for the running Martlet. Never a link ID or secret.</summary>
    private static object ShareLinksSummary(string directory)
    {
        string? protocol = null;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\martlet\shell\open\command");
                protocol = key?.GetValue(null) as string;
            }
            catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        var pending = File.Exists(Path.Combine(directory, "pending-share-link.txt"));
        try
        {
            var path = Path.Combine(directory, "share-links.json");
            if (!File.Exists(path)) return new { made = Array.Empty<object>(), accepted = Array.Empty<object>(), protocolRegistered = protocol is not null, pendingHandOver = pending };
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            static string[]? List(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : null;
            return new
            {
                made = root.TryGetProperty("made", out var made) && made.ValueKind == JsonValueKind.Array
                    ? made.EnumerateArray().Select(l => (object)new { label = Text(l, "label"), allHosts = List(l, "hosts") is null, hosts = List(l, "hosts"), createdAt = Text(l, "created_at") }).ToArray()
                    : [],
                accepted = root.TryGetProperty("accepted", out var accepted) && accepted.ValueKind == JsonValueKind.Array
                    ? accepted.EnumerateArray().Select(l => (object)new
                    {
                        from = Text(l, "from"), label = Text(l, "label"), hosts = List(l, "host_ids") ?? [], forgotten = List(l, "skipped") ?? [],
                        acceptedAt = Text(l, "accepted_at")
                    }).ToArray()
                    : [],
                protocolRegistered = protocol is not null,
                pendingHandOver = pending
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return "unreadable"; }
    }

    /// <summary>router-port.json in short (docs/NETWORK.md, "Opening a port on your router"): the port Martlet opened on the router
    /// for this PC's host service (which host, the router's name, the external and internal port, when it was opened and last
    /// renewed, the last problem and whether this PC reached it from inside). Never an address. Null when none is open.</summary>
    private static object? RouterPortSummary(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "router-port.json");
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            static int? Number(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : null;
            return new
            {
                hostId = Text(root, "host_id"), router = Text(root, "router_name"), externalPort = Number(root, "external_port"),
                internalPort = Number(root, "internal_port"), openedAt = Text(root, "opened_at"), renewedAt = Text(root, "renewed_at"),
                problem = Text(root, "problem"),
                answeredFromInside = root.TryGetProperty("answered_from_inside", out var answered) && answered.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? answered.GetBoolean() : (bool?)null
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return "unreadable"; }
    }

    /// <summary>household-signin.json: the household's sign-in providers as the desktop last read them from its hosts (each
    /// provider's ID, kind, name, the hosts that have it, miss it, have other settings or lack its client secret, and whether that
    /// desktop keeps its secret to add it to new hosts) and the providers that desktop set up; never a secret. Null when the
    /// desktop hasn't read it.</summary>
    private static object? HouseholdSignInSummary(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "household-signin.json");
            return File.Exists(path) ? JsonSerializer.Deserialize<JsonElement>(File.ReadAllBytes(path)) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return "unreadable"; }
    }

    /// <summary>This data folder's device ID, read-only (Martlet.Core's DeviceIds): saved in device.json ("saved"), or the one the
    /// desktop will keep from its pairings ("pairings") or an older Martlet's desktop-&lt;pc name&gt; ("legacy"); null with "new"
    /// when the desktop picks a new desktop-&lt;pc name&gt;-&lt;6 of [a-z0-9]&gt; one on its next start.</summary>
    private static object DeviceSummary(string directory) => Martlet.Core.Network.DeviceIds.Peek(directory) is { } choice
        ? new { id = (string?)choice.Id, source = choice.Source.ToString().ToLowerInvariant() }
        : new { id = (string?)null, source = "new" };

    /// <summary>hosts.json in short: each paired host's ID, how many outside addresses are kept with its pairing and its access
    /// ("friend" for a host a friend shares with this PC: its engines only, never in this PC's network; "member" for your own).</summary>
    private static object? PairedHostsSummary(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "hosts.json");
            if (!File.Exists(path)) return Array.Empty<object>();
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.GetProperty("hosts").EnumerateArray().Select(h => new
            {
                hostId = h.GetProperty("pairing").GetProperty("hostId").GetString(),
                outsideAddresses = h.TryGetProperty("outsideAddresses", out var outside) && outside.ValueKind == JsonValueKind.Array
                    ? outside.GetArrayLength() : 0,
                access = IsSharedHost(h) ? "friend" : "member",
                // Found at another home address than its paired one (its computer got a new address); the address isn't returned.
                movedOnHomeNetwork = h.TryGetProperty("movedTo", out var moved) && moved.ValueKind == JsonValueKind.String
            }).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return "unreadable";
        }
    }

    /// <summary>friends.json: what Share › Friends last read from each of your hosts (people's labels, providers, how many of
    /// their computers signed in, how many asked); null when the desktop hasn't read it.</summary>
    private static object? FriendsSummary(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "friends.json");
            return File.Exists(path) ? JsonSerializer.Deserialize<JsonElement>(File.ReadAllBytes(path)) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return "unreadable"; }
    }

    /// <summary>Probes each roster host's home and outside addresses (pinned TLS, GET /health/live) when contactHosts is true.</summary>
    private static async Task<object> OutsideReachabilityAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var directory = DataDirectory(arguments);
        var contact = OptionalBool(arguments, "contactHosts") == true;
        var path = Path.Combine(directory, Martlet.Avatar.Audio2Face.Remote.NetworkLocalState.FileName);
        Martlet.Core.Network.NetworkRoster? roster = null;
        try { if (File.Exists(path)) roster = Martlet.Avatar.Audio2Face.Remote.NetworkLocalState.Parse(File.ReadAllBytes(path)).Roster; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException)
        {
            return new { state = "unreadable" };
        }
        if (roster is null) return new { state = "none", hosts = Array.Empty<object>() };
        var hosts = new List<object>();
        foreach (var host in roster.ActiveHosts.Where(h => h.Origin is not null && h.Spki is not null))
        {
            var outside = host.Addresses ?? [];
            if (!contact)
            {
                hosts.Add(new { id = host.Id, outsideAddresses = outside.Count, checkedNow = false });
                continue;
            }
            var timeout = TimeSpan.FromSeconds(4);
            var probes = await Task.WhenAll(new string?[] { null }.Concat(outside).Select(address =>
                Martlet.Avatar.Audio2Face.Remote.HostRoutes.ProbeAsync(host.Origin!, host.Spki!, address, timeout, cancellation)));
            var home = probes[0];
            var outsideResults = probes.Skip(1).Select((p, i) => new { address = i + 1, reachable = p.Reachable, ms = p.Milliseconds, problem = p.Problem }).ToArray();
            var use = home.Reachable ? "home" : outsideResults.FirstOrDefault(o => o.reachable) is { } first ? $"outside {first.address}" : "none";
            hosts.Add(new
            {
                id = host.Id, outsideAddresses = outside.Count, checkedNow = true,
                home = new { reachable = home.Reachable, ms = home.Milliseconds, problem = home.Problem },
                outside = outsideResults, wouldUse = use
            });
        }
        return new { state = "member", contacted = contact, hosts };
    }

    /// <summary>"Let my other computers find this PC" as the desktop keeps it (the file names match Martlet.Desktop's Nearby and
    /// HostRegistry): the choice, on unless nearby.txt says "off", and the paired hosts this PC could share (those it runs, saved
    /// as ThisPcDocker, or reaches over SSH). Host IDs and how each is reached only: no addresses, SSH targets or keys.</summary>
    private static object NearbyStatus(JsonElement arguments)
    {
        var directory = DataDirectory(arguments);
        string? choice;
        try { choice = File.ReadAllText(Path.Combine(directory, "nearby.txt")).Trim(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { choice = null; }
        object hosts;
        var path = Path.Combine(directory, "hosts.json");
        if (!File.Exists(path)) hosts = new { state = "none", paired = 0, shareable = Array.Empty<object>() };
        else
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                var list = document.RootElement.GetProperty("hosts").EnumerateArray().Select(host =>
                {
                    var method = host.TryGetProperty("method", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : "OnHost";
                    var ssh = host.TryGetProperty("sshTarget", out var target) && target.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(target.GetString());
                    var id = host.GetProperty("pairing").GetProperty("hostId").GetString();
                    var shareable = method == "ThisPcDocker" || method is "SshDocker" or "SshNative" && ssh;
                    return (id, method, shareable);
                }).ToArray();
                hosts = new
                {
                    state = "loaded", paired = list.Length,
                    shareable = list.Where(h => h.shareable).Select(h => new { hostId = h.id, reach = h.method }).ToArray()
                };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or
                InvalidOperationException)
            {
                hosts = new { state = "unreadable" };
            }
        }
        // How Add a computer looks (Martlet.Desktop's Nearby): it lists each computer as it answers, for this long, and a
        // computer offers at most as many hosts as Martlet can remember.
        return new
        {
            share = choice switch { "off" => "off", null => "on (default)", _ => "on" }, port = 9444, hosts,
            find = new { listensSeconds = 15, queriesSentAtSeconds = new[] { 0, 0.5, 1.5, 3.5, 7.5 }, hostsOfferedAtMost = 64 }
        };
    }

    /// <summary>hosts_find: one host find on this PC's home network (<see cref="Martlet.Core.Network.HostFind"/>), with each answer
    /// compared to the pairings in the given data folder's hosts.json (paired origin and where it moved to). No keys or secrets.</summary>
    private static async Task<object> HostsFindAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var seconds = arguments.TryGetProperty("seconds", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var s)
            ? Math.Clamp(s, 0.5, 5) : 2;
        var paired = new Dictionary<string, (string Address, string? MovedTo)>(StringComparer.Ordinal);
        string? pairings = null;
        if (arguments.TryGetProperty("dataDirectory", out var given) && given.ValueKind == JsonValueKind.String)
        {
            pairings = "none";
            try
            {
                var path = Path.Combine(DataDirectory(arguments), "hosts.json");
                if (File.Exists(path))
                {
                    using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                    foreach (var host in document.RootElement.GetProperty("hosts").EnumerateArray())
                    {
                        var pairing = host.GetProperty("pairing");
                        var origin = new Uri(pairing.GetProperty("origin").GetString()!);
                        var moved = host.TryGetProperty("movedTo", out var to) && to.ValueKind == JsonValueKind.String ? to.GetString() : null;
                        paired[pairing.GetProperty("hostId").GetString()!] = (origin.Host, moved?[..moved.LastIndexOf(':')]);
                    }
                    pairings = "loaded";
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or
                InvalidOperationException or UriFormatException or ArgumentException)
            {
                pairings = "unreadable";
            }
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var seen = await Martlet.Core.Network.HostFind.FindAsync(null, TimeSpan.FromSeconds(seconds), cancellation);
        var answers = seen.Select(sighting =>
        {
            var address = sighting.Address.ToString();
            var where = !paired.TryGetValue(sighting.HostId, out var host) ? (pairings is null ? null : "not paired here")
                : address == host.Address ? "at its paired address"
                : address == host.MovedTo ? "at the address saved as where it moved to"
                : System.Net.IPAddress.IsLoopback(sighting.Address) ? "on this PC (answered over loopback)"
                : "somewhere new (Martlet checks its pinned key there before it uses it)";
            return new { hostId = sighting.HostId, address, where };
        }).ToArray();
        return new
        {
            port = Martlet.Core.Network.HostFind.Port, listenedSeconds = seconds, tookMs = clock.ElapsedMilliseconds,
            targets = Martlet.Core.Network.HostFind.BroadcastTargets().Select(t => t.ToString()).ToArray(),
            pairings, answers
        };
    }
}
