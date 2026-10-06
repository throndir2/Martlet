using System.Security.Cryptography;
using System.Text;
using Martlet.Companion.Platform;
using Martlet.Platform.Linux.DBus;
using Tmds.DBus.Protocol;

namespace Martlet.Platform.Linux.Credentials;

/// <summary>Keys and secrets in the desktop's Secret Service (GNOME Keyring, KWallet 5.97+, KeePassXC) over D-Bus
/// (org.freedesktop.secrets), the same store libsecret uses. Items are labelled "Martlet: name" and found by the attributes
/// application=io.github.throndir2.Martlet and name. The secret travels over the private session bus in the "plain"
/// transfer mode. A locked keyring shows the desktop's unlock prompt.</summary>
internal sealed class SecretServiceStore(FeatureStatus status) : ICredentialStore, IDisposable
{
    private const string Service = "org.freedesktop.secrets";
    private const string ServicePath = "/org/freedesktop/secrets";
    private const string ServiceInterface = "org.freedesktop.Secret.Service";
    private static readonly TimeSpan PromptTimeout = TimeSpan.FromMinutes(3);
    private readonly SemaphoreSlim gate = new(1, 1);
    private SessionBus? bus;
    private string? transferSession;

    public FeatureStatus Status { get; } = status;
    public bool IsPersistent => true;

    internal static Dictionary<string, string> Attributes(string name) => new(StringComparer.Ordinal)
    {
        ["xdg:schema"] = LinuxDesktopIds.AppId + ".Secret",
        ["application"] = LinuxDesktopIds.AppId,
        ["name"] = name
    };

    internal static void Validate(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 128 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':' or '/'))
            throw new ArgumentException("Credential names are short ASCII ids.", nameof(name));
    }

    public async Task<string?> GetAsync(string name, CancellationToken cancellationToken)
    {
        Validate(name);
        return await Locked(async bus =>
        {
            var items = await SearchAsync(bus, name).ConfigureAwait(false);
            if (items.Count == 0) return null;
            await UnlockAsync(bus, items, cancellationToken).ConfigureAwait(false);
            var session = transferSession!;
            var bytes = await bus.CallAsync(bus.Call(Service, items[0], "org.freedesktop.Secret.Item", "GetSecret", "o",
                (ref MessageWriter w) => w.WriteObjectPath(session)), static (m, _) =>
                {
                    var reader = m.GetBodyReader();
                    reader.ReadObjectPath();
                    reader.ReadArrayOfByte();
                    return reader.ReadArrayOfByte();
                }).ConfigureAwait(false);
            try { return Encoding.UTF8.GetString(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetAsync(string name, string secret, CancellationToken cancellationToken)
    {
        Validate(name);
        ArgumentNullException.ThrowIfNull(secret);
        await Locked(async bus =>
        {
            var collection = await DefaultCollectionAsync(bus, cancellationToken).ConfigureAwait(false);
            await UnlockAsync(bus, [collection], cancellationToken).ConfigureAwait(false);
            var value = Encoding.UTF8.GetBytes(secret);
            try
            {
                var session = transferSession!;
                var (_, prompt) = await bus.CallAsync(bus.Call(Service, collection, "org.freedesktop.Secret.Collection", "CreateItem",
                    "a{sv}(oayays)b", (ref MessageWriter w) => WriteItem(ref w, name, session, value)), static (m, _) =>
                    {
                        var reader = m.GetBodyReader();
                        return (reader.ReadObjectPathAsString(), reader.ReadObjectPathAsString());
                    }).ConfigureAwait(false);
                await PromptAsync(bus, prompt, cancellationToken).ConfigureAwait(false);
            }
            finally { CryptographicOperations.ZeroMemory(value); }
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>CreateItem's arguments: label and lookup attributes, the secret struct (session, no parameters, value,
    /// content type) and replace=true so saving again overwrites.</summary>
    internal static void WriteItem(ref MessageWriter writer, string name, string session, byte[] value)
    {
        writer.WriteDictionary(new Dictionary<string, VariantValue>
        {
            ["org.freedesktop.Secret.Item.Label"] = VariantValue.String($"Martlet: {name}"),
            ["org.freedesktop.Secret.Item.Attributes"] = new Dict<string, string>(Attributes(name)).AsVariantValue()
        });
        writer.WriteStructureStart();
        writer.WriteObjectPath(session);
        writer.WriteArray(Array.Empty<byte>());
        writer.WriteArray(value);
        writer.WriteString("text/plain; charset=utf8");
        writer.WriteBool(true);
    }

    public async Task DeleteAsync(string name, CancellationToken cancellationToken)
    {
        Validate(name);
        await Locked(async bus =>
        {
            foreach (var item in await SearchAsync(bus, name).ConfigureAwait(false))
            {
                var prompt = await bus.CallAsync(bus.Call(Service, item, "org.freedesktop.Secret.Item", "Delete"),
                    static (m, _) => m.GetBodyReader().ReadObjectPathAsString()).ConfigureAwait(false);
                await PromptAsync(bus, prompt, cancellationToken).ConfigureAwait(false);
            }
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> Locked<T>(Func<SessionBus, Task<T>> action, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var connected = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                try { return await action(connected).ConfigureAwait(false); }
                catch (DBusConnectionClosedException) when (attempt == 0) { Reset(); }
                catch (DBusErrorReplyException reply)
                {
                    throw new InvalidOperationException($"The Secret Service answered {reply.ErrorName}: {reply.ErrorMessage}", reply);
                }
            }
        }
        finally { gate.Release(); }
    }

    private async Task<SessionBus> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (bus is not null && transferSession is not null) return bus;
        Reset();
        var (connected, error) = await SessionBus.ConnectAsync(cancellationToken).ConfigureAwait(false);
        bus = connected ?? throw new InvalidOperationException($"The Secret Service is unreachable: {error}");
        transferSession = await bus.CallAsync(bus.Call(Service, ServicePath, ServiceInterface, "OpenSession", "sv", (ref MessageWriter w) =>
        {
            w.WriteString("plain");
            w.WriteVariantString("");
        }), static (m, _) =>
        {
            var reader = m.GetBodyReader();
            reader.ReadVariantValue();
            return reader.ReadObjectPathAsString();
        }).ConfigureAwait(false);
        return bus;
    }

    private static async Task<List<string>> SearchAsync(SessionBus bus, string name)
    {
        var attributes = Attributes(name);
        var (unlocked, locked) = await bus.CallAsync(bus.Call(Service, ServicePath, ServiceInterface, "SearchItems", "a{ss}",
            (ref MessageWriter w) =>
            {
                var start = w.WriteDictionaryStart();
                foreach (var (key, value) in attributes)
                {
                    w.WriteDictionaryEntryStart();
                    w.WriteString(key);
                    w.WriteString(value);
                }
                w.WriteDictionaryEnd(start);
            }), static (m, _) =>
            {
                var reader = m.GetBodyReader();
                return (reader.ReadArrayOfObjectPath(), reader.ReadArrayOfObjectPath());
            }).ConfigureAwait(false);
        return [.. unlocked.Concat(locked).Select(p => p.ToString())];
    }

    private static async Task<string> DefaultCollectionAsync(SessionBus bus, CancellationToken cancellationToken)
    {
        var alias = await bus.CallAsync(bus.Call(Service, ServicePath, ServiceInterface, "ReadAlias", "s",
            (ref MessageWriter w) => w.WriteString("default")), static (m, _) => m.GetBodyReader().ReadObjectPathAsString()).ConfigureAwait(false);
        if (alias != "/") return alias;
        // A fresh account has no keyring yet: create the default ("Login") one; the desktop asks for its password.
        var (collection, prompt) = await bus.CallAsync(bus.Call(Service, ServicePath, ServiceInterface, "CreateCollection", "a{sv}s",
            (ref MessageWriter w) =>
            {
                w.WriteDictionary(new Dictionary<string, VariantValue> { ["org.freedesktop.Secret.Collection.Label"] = VariantValue.String("Login") });
                w.WriteString("default");
            }), static (m, _) =>
            {
                var reader = m.GetBodyReader();
                return (reader.ReadObjectPathAsString(), reader.ReadObjectPathAsString());
            }).ConfigureAwait(false);
        var result = await PromptAsync(bus, prompt, cancellationToken).ConfigureAwait(false);
        if (collection == "/" && result is { Type: VariantValueType.ObjectPath } path) collection = path.GetObjectPathAsString();
        return collection != "/" ? collection : throw new InvalidOperationException("No keyring could be created.");
    }

    private static async Task UnlockAsync(SessionBus bus, IReadOnlyList<string> objects, CancellationToken cancellationToken)
    {
        var paths = objects.Select(o => new ObjectPath(o)).ToArray();
        var prompt = await bus.CallAsync(bus.Call(Service, ServicePath, ServiceInterface, "Unlock", "ao",
            (ref MessageWriter w) => w.WriteArray(paths)), static (m, _) =>
            {
                var reader = m.GetBodyReader();
                reader.ReadArrayOfObjectPath();
                return reader.ReadObjectPathAsString();
            }).ConfigureAwait(false);
        await PromptAsync(bus, prompt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Shows a Secret Service prompt (unlock, create) and waits for the user. "/" means none is needed.</summary>
    private static async Task<VariantValue?> PromptAsync(SessionBus bus, string prompt, CancellationToken cancellationToken)
    {
        if (prompt is "/" or "") return null;
        var completed = new TaskCompletionSource<(bool Dismissed, VariantValue Result)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = await bus.WatchAsync(prompt, "org.freedesktop.Secret.Prompt", "Completed", static (m, _) =>
        {
            var reader = m.GetBodyReader();
            return (reader.ReadBool(), reader.ReadVariantValue());
        }, value => completed.TrySetResult(value)).ConfigureAwait(false);
        await bus.Connection.CallMethodAsync(bus.Call(Service, prompt, "org.freedesktop.Secret.Prompt", "Prompt", "s",
            (ref MessageWriter w) => w.WriteString(""))).ConfigureAwait(false);
        var (dismissed, result) = await completed.Task.WaitAsync(PromptTimeout, cancellationToken).ConfigureAwait(false);
        if (dismissed) throw new UnauthorizedAccessException("The keyring prompt was dismissed.");
        return result;
    }

    private void Reset()
    {
        bus?.Dispose();
        bus = null;
        transferSession = null;
    }

    public void Dispose() => Reset();
}
