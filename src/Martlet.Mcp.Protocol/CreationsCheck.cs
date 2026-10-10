using System.IO;
using System.Text.Json;
using Martlet.Conversation;
using Martlet.Core.Creations;

namespace Martlet.Mcp;

/// <summary>MCP's view of Martlet's creations (docs/CREATIONS.md): <see cref="Status"/> reads a data directory's copy, and
/// <see cref="RunAsync"/> checks Martlet's creation tools in process and runs the sync rehearsal. Never returns a title, text,
/// voice or personality: creations are the owner's own, like memories.</summary>
internal static class CreationsCheck
{
    /// <summary>Where the creations of <paramref name="dataDirectory"/> are: the folder of the account in use on this device
    /// (accounts\session.json), else of the account they moved to (creations-moved.json), else the data folder itself.</summary>
    internal static string Folder(string dataDirectory) =>
        (Signed(dataDirectory) ?? CreationAccounts.Moved(dataDirectory)?.Account) is { } account
            ? CreationAccounts.Folder(dataDirectory, account)
            : dataDirectory;

    // The account in use on this device; null without a readable session.
    private static Guid? Signed(string dataDirectory)
    {
        try { return Martlet.Core.Accounts.AccountSessionState.Load(dataDirectory)?.Current; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException) { return null; }
    }

    /// <summary>Reads the creations of <paramref name="account"/> (32 hex digits or a GUID) in &lt;data&gt;\accounts\&lt;32 hex&gt;.
    /// Without an account it reads the account in use on this device (accounts\session.json), else the account the data folder's
    /// creations moved to (creations-moved.json), else the data folder itself. The answer names the account and the folder it read (docs/ACCOUNTS.md).</summary>
    internal static object Status(string dataDirectory, string? account = null)
    {
        Guid? accountId = null;
        if (account is not null)
            accountId = Guid.TryParse(account, out var parsed) ? parsed : throw new ArgumentException("account is an account ID (32 hex digits or a GUID).");
        var moved = CreationAccounts.Moved(dataDirectory);
        var signedIn = Signed(dataDirectory);
        accountId ??= signedIn ?? moved?.Account;
        var folder = accountId is { } id ? CreationAccounts.Folder(dataDirectory, id) : dataDirectory;
        var from = new
        {
            account = accountId?.ToString("N"),
            folder = accountId is { } shown ? Path.Combine(CreationAccounts.AccountsDirectoryName, shown.ToString("N")) : ".",
            chosenBy = account is not null ? "argument" : signedIn is not null ? "signed in" : moved is not null ? "moved" : "data folder (before accounts)",
            moved = moved is null ? null : new
            {
                account = moved.Account.ToString("N"), movedAt = moved.MovedAt, creations = moved.Creations, assets = moved.Assets
            },
            leftInDataFolder = moved is not null && File.Exists(Path.Combine(dataDirectory, CreationStore.LibraryFile))
        };
        dataDirectory = folder;
        var path = Path.Combine(dataDirectory, CreationStore.LibraryFile);
        CreationLibrary? library = null;
        string state;
        if (!File.Exists(path)) state = "none";
        else
        {
            library = CreationStore.Load(dataDirectory);
            state = library is null ? "unreadable" : "loaded";
        }
        library ??= CreationLibrary.Empty;
        var sync = CreationSyncState.Load(dataDirectory);
        var root = CreationStore.Root(dataDirectory);
        var files = Directory.Exists(root) ? new DirectoryInfo(root).GetFiles() : [];
        var live = library.LiveAssets();
        var incoming = Path.Combine(dataDirectory, CreationStore.IncomingDirectoryName);
        return new
        {
            from,
            state,
            live = library.Live.Count,
            tombstones = library.Creations.Count(c => c.Removed),
            totalBytes = library.LiveBytes,
            revision = library.Revision,
            digest = library.Digest()[..16],
            kinds = library.Live.GroupBy(c => c.Kind).Select(g => new { kind = g.Key, count = g.Count(), bytes = g.Sum(c => c.Bytes) }),
            creations = library.Live.Select(c => new
            {
                key = c.Key,
                kind = c.Kind,
                kindVersion = c.KindVersion,
                bytes = c.Bytes,
                durationMs = c.DurationMs,
                createdAt = c.CreatedAt,
                createdOn = c.CreatedBy?.Device,
                autoCleanup = c.AutoCleanup,
                assets = c.Assets!.Select(a => new { name = a.Name, mediaType = a.MediaType, bytes = a.Bytes, pieces = a.Chunks.Count }),
                completeHere = CreationStore.IsComplete(dataDirectory, c),
                onHosts = sync?.Hosts.Count(h => h.Complete.Contains(c.Id))
            }),
            storage = new
            {
                files = files.Length,
                bytes = files.Sum(f => f.Length),
                unused = files.Count(f => !live.ContainsKey(Path.GetFileNameWithoutExtension(f.Name))),
                copying = Directory.Exists(incoming) ? Directory.GetDirectories(incoming).Length : 0
            },
            sync = sync is null ? null : new
            {
                checkedAt = sync.CheckedAt,
                summary = sync.Summary,
                hosts = sync.Hosts.Select(h => new
                {
                    hostId = h.HostId, state = h.State, at = h.At, complete = h.Complete.Count,
                    missing = library.Live.Count(c => !h.Complete.Contains(c.Id))
                })
            },
            limits = new
            {
                creations = CreationLibrary.MaximumCreations, totalBytes = CreationLibrary.MaximumTotalBytes,
                creationBytes = CreationLibrary.MaximumCreationBytes, assetBytes = CreationLibrary.MaximumAssetBytes,
                pieceBytes = CreationLibrary.ChunkBytes, tombstones = CreationLibrary.MaximumTombstones
            },
            tools = "list_creations and perform_creation are offered to every tool-capable reply while Martlet has a kind of creation " +
                "registered (none in this MCP process; the desktop registers its kinds at startup)."
        };
    }

    /// <summary>Writes two FIXTURE - NOT AI test tones into <paramref name="dataDirectory"/> (a disposable folder under the
    /// temporary folder, never Martlet's own), so the Creations page can be checked with <c>-Desktop</c>.</summary>
    internal static async Task<object> SeedAsync(string dataDirectory, CancellationToken cancellation)
    {
        var full = Path.GetFullPath(dataDirectory);
        if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("seedDataDirectory must be a disposable folder under the temporary folder.");
        var registry = new CreationRegistry();
        registry.Register(FixtureCreations.Kind);
        var author = new CreationAuthor { Device = "lab-desktop", Computer = "LAB-DESKTOP", Persona = "Fixture" };
        var first = await CreationStore.AddAsync(full, FixtureCreations.Draft(author, "Fixture morning tone", 3, 440), registry,
            DateTimeOffset.UtcNow.AddMinutes(-5), cancellation);
        var second = await CreationStore.AddAsync(full, FixtureCreations.Draft(author, "Fixture evening tone", 75, 220), registry,
            DateTimeOffset.UtcNow, cancellation);
        return new { seeded = new[] { second.Key, first.Key }, kind = FixtureCreations.KindName };
    }

    /// <summary>Checks list_creations and perform_creation with the production tool code, store and FIXTURE - NOT AI kind on a
    /// disposable folder, then runs <paramref name="rehearsal"/> (the sync between two gateways and three desktops).</summary>
    internal static async Task<object> RunAsync(Func<Task<object>> rehearsal, CancellationToken cancellation)
    {
        var steps = new List<object>();
        var ok = true;
        void Step(string name, bool passed, string detail)
        {
            ok &= passed;
            steps.Add(new { step = name, ok = passed, detail });
        }

        var root = Path.Combine(Path.GetTempPath(), "martlet-creations-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new CreationRegistry();
            Step("With no kind registered Martlet offers no creation tools (requests stay exactly as before)", !registry.HasKinds,
                $"kinds registered: {registry.Kinds.Count}");
            registry.Register(FixtureCreations.Kind);
            registry.Register(FixtureCreations.Kind);
            var first = CreationTools.Definitions(registry.Kinds);
            var second = CreationTools.Definitions(registry.Kinds);
            var stable = first.Zip(second).All(p => p.First.Name == p.Second.Name && p.First.Description == p.Second.Description &&
                p.First.ParametersJson == p.Second.ParametersJson);
            Step("The tools are the same two, in the same order and words, every time (the request start stays the same)",
                stable && first.Select(d => d.Name).SequenceEqual([CreationTools.ListName, CreationTools.PerformName]),
                $"{string.Join(", ", first.Select(d => $"{d.Name} ({d.Utf8Bytes} bytes)"))}; identical on every build: {stable}");

            var author = new CreationAuthor { Device = "lab-desktop", Computer = "LAB-DESKTOP" };
            var tone = await CreationStore.AddAsync(root, FixtureCreations.Draft(author, "Morning tone", 2, 440), registry,
                DateTimeOffset.UtcNow.AddMinutes(-1), cancellation);
            var other = await CreationStore.AddAsync(root, FixtureCreations.Draft(author, "Evening tone", 1, 220), registry,
                DateTimeOffset.UtcNow, cancellation);
            bool Here(Creation c) => CreationStore.IsComplete(root, c);
            var library = CreationStore.View(root);
            var all = CreationTools.List(library, registry, "{}", Here);
            var morning = CreationTools.List(library, registry, "{\"query\":\"morning\"}", Here);
            var none = CreationTools.List(library, registry, "{\"kind\":\"song\"}", Here);
            Step("list_creations lists them newest first with short ids, and filters by words and kind",
                !all.IsError && Count(all) == 2 && all.Output.IndexOf(other.Key, StringComparison.Ordinal) < all.Output.IndexOf(tone.Key, StringComparison.Ordinal) &&
                Count(morning) == 1 && morning.Output.Contains(tone.Key, StringComparison.Ordinal) && Count(none) == 0,
                $"all: {Count(all)}, \"morning\": {Count(morning)}, songs: {Count(none)}; ids are {tone.Key.Length} hex digits");

            var withoutHandler = await CreationTools.PerformAsync(library, registry, $"{{\"id\":\"{tone.Key}\"}}", c => CreationStore.Assets(root, c), cancellation);
            Step("perform_creation without a handler says plainly it can't be done here", withoutHandler.IsError &&
                withoutHandler.Output.Contains("can't play test tones", StringComparison.Ordinal), withoutHandler.Output);

            using (registry.Handle(FixtureCreations.KindName, FixtureCreations.Handler))
            {
                var performed = await CreationTools.PerformAsync(library, registry, $"{{\"id\":\"{tone.Key}\",\"options\":{{\"start_ms\":500}}}}",
                    c => CreationStore.Assets(root, c), cancellation);
                Step("perform_creation hands the creation and options to its kind's handler, which reads this computer's copy",
                    !performed.IsError && performed.Output.Contains("from 500 ms", StringComparison.Ordinal), performed.Output);
                var unknownId = await CreationTools.PerformAsync(library, registry, "{\"id\":\"abcdef123456\"}", c => CreationStore.Assets(root, c), cancellation);
                var badArguments = await CreationTools.PerformAsync(library, registry, "not json", c => CreationStore.Assets(root, c), cancellation);
                var badOptions = await CreationTools.PerformAsync(library, registry, $"{{\"id\":\"{tone.Key}\",\"options\":3}}",
                    c => CreationStore.Assets(root, c), cancellation);
                Step("Unknown ids, bad arguments and bad options get clear refusals",
                    unknownId.IsError && unknownId.Output.Contains("list_creations", StringComparison.Ordinal) && badArguments.IsError && badOptions.IsError,
                    $"{unknownId.Output} | {badArguments.Output} | {badOptions.Output}");

                File.Delete(CreationStore.AssetPath(root, other.Asset("audio")!.Sha256));
                var copying = await CreationTools.PerformAsync(library, registry, $"{{\"id\":\"{other.Key}\"}}", c => CreationStore.Assets(root, c), cancellation);
                var listed = CreationTools.List(library, registry, "{}", Here);
                Step("A creation still copying to this computer isn't performed, and list_creations says so",
                    copying.IsError && copying.Output.Contains("still copying", StringComparison.Ordinal) &&
                    listed.Output.Contains("Still copying", StringComparison.Ordinal), copying.Output);

                var future = new Creation
                {
                    Id = CreationLibrary.NewId(), Kind = "hologram", KindVersion = 2, Title = "Future", CreatedBy = author,
                    CreatedAt = DateTimeOffset.UtcNow, Assets = [CreationLibrary.Asset("scene", "application/octet-stream", new byte[] { 1, 2, 3 })],
                    Revision = 1, UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = author.Device
                };
                var withFuture = library.Add(future, DateTimeOffset.UtcNow);
                var unknownKind = await CreationTools.PerformAsync(withFuture, registry, $"{{\"id\":\"{future.Key}\"}}", c => CreationStore.Assets(root, c), cancellation);
                Step("A kind this Martlet doesn't know gets a clear refusal (it may need an update)",
                    unknownKind.IsError && unknownKind.Output.Contains("doesn't know", StringComparison.Ordinal), unknownKind.Output);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException or
            InvalidOperationException or JsonException)
        {
            Step("The tool check finished", false, $"{error.GetType().Name}: {error.Message}");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }

        var sync = await rehearsal();
        var syncOk = sync.GetType().GetProperty("exitCode")?.GetValue(sync) is 0;
        return new
        {
            ok = ok && syncOk,
            tools = new { ok, steps },
            sync
        };

        static int Count(ConversationToolResult result)
        {
            try
            {
                using var document = JsonDocument.Parse(result.Output[..result.Output.IndexOf('\n')]);
                return document.RootElement.GetProperty("count").GetInt32();
            }
            catch (Exception error) when (error is JsonException or ArgumentOutOfRangeException or KeyNotFoundException) { return -1; }
        }
    }
}
