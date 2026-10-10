using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

#if MARTLET_MCP
using Martlet.Mcp.Logging;

namespace Martlet.Mcp.Shared;
#else
namespace Martlet.Desktop;
#endif

internal enum AccountUnlockMethod { Pin, Password, WindowsHello }

internal enum AccountUnlockStatus
{
    Unlocked,
    /// <summary>The PIN or password is wrong.</summary>
    Wrong,
    /// <summary>Too many wrong tries: wait until <see cref="AccountUnlockResult.RetryAfter"/>.</summary>
    Wait,
    /// <summary>That way to unlock isn't set up on this PC for the account.</summary>
    NotSet,
    /// <summary>The password is right, but only the PIN opens the account's encrypted files here.</summary>
    NeedsPin
}

/// <summary>An unlock's outcome; <see cref="FileKey"/> is the account's file key when its files are encrypted here.</summary>
internal sealed record AccountUnlockResult(AccountUnlockStatus Status, byte[]? FileKey = null, DateTimeOffset? RetryAfter = null);

/// <summary>
/// How one account unlocks on this PC (docs/ACCOUNTS.md, Unlock): device scope, never synced, in
/// <c>&lt;data&gt;\account-locks\&lt;32 hex&gt;.json</c>, outside the account's own folder so it stays readable while that folder
/// is encrypted. The choices are plain JSON (MCP's <c>account_security_status</c> reads them); the PIN and password verifiers
/// and the wrapped file keys are in <see cref="Protected"/> (DPAPI for this Windows user). Never a PIN or password itself.
/// </summary>
internal sealed record AccountLock
{
    public int SchemaVersion { get; init; } = 1;
    public Guid AccountId { get; init; }
    /// <summary>*Ask for my password on this PC*: the Windows login doesn't unlock the account here.</summary>
    public bool AskOnThisPc { get; init; }
    /// <summary>*Remember me on this PC*: off, the account signs out of this PC when Martlet closes.</summary>
    public bool Remember { get; init; } = true;
    public bool Hello { get; init; }
    public bool HasPin { get; init; }
    public bool HasPassword { get; init; }
    /// <summary>*Encrypt my files on this PC while locked*.</summary>
    public bool Encrypt { get; init; }
    public int Failures { get; init; }
    public DateTimeOffset? RetryAfter { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public string? Protected { get; init; }

    /// <summary>The ways to unlock set up here: "pin", "password", "hello".</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Methods =>
        new[] { HasPin ? "pin" : null, HasPassword ? "password" : null, Hello && !Encrypt ? "hello" : null }.OfType<string>().ToArray();

    /// <summary>Whether switching to the account here needs an Unlock. <paramref name="windowsLoginOfThisPc"/>: the account has
    /// this PC's Windows login (this device ID and SID) among its logins, so the Windows login unlocks it unless the person asked
    /// for the password here or encrypted the files.</summary>
    public bool NeedsUnlock(bool windowsLoginOfThisPc) => !windowsLoginOfThisPc || AskOnThisPc || Encrypt;
}

/// <summary>The protected part of an <see cref="AccountLock"/>.</summary>
internal sealed record AccountLockSecrets
{
    public AccountSecretVerifier? Pin { get; init; }
    public AccountSecretVerifier? Password { get; init; }
    public AccountKeyWrap? PinKey { get; init; }
    public AccountKeyWrap? PasswordKey { get; init; }
}

/// <summary>The unlock files of the accounts signed in on this PC (<see cref="AccountLock"/>). PIN: 4 to 12 digits, Martlet's own
/// for that account (not the Windows PIN). Wrong PINs and passwords: <see cref="FreeTries"/> free, then a wait that doubles from
/// one second up to <see cref="LongestWait"/>, kept across restarts. Thread-safe.</summary>
internal sealed class AccountLockStore(string dataDirectory, int iterations = AccountProtection.Iterations, TimeProvider? clock = null)
{
    internal const string Folder = "account-locks";
    internal const int FreeTries = 5;
    internal const int MinimumPasswordLength = 12;
    internal static readonly TimeSpan LongestWait = TimeSpan.FromMinutes(15);
    private static readonly object Gate = new();
    private readonly TimeProvider time = clock ?? TimeProvider.System;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    internal string Directory => Path.Combine(dataDirectory, Folder);

    internal static bool IsPin(string? pin) => pin is { Length: >= 4 and <= 12 } && pin.All(char.IsAsciiDigit);

    internal string PathFor(Guid accountId) => Path.Combine(Directory, accountId.ToString("N") + ".json");

    /// <summary>The account's unlock file; a new one (nothing set) when there is none or it can't be read.</summary>
    internal AccountLock Load(Guid accountId)
    {
        lock (Gate)
        {
            var path = PathFor(accountId);
            try
            {
                var loaded = JsonSerializer.Deserialize<AccountLock>(File.ReadAllBytes(path), Json);
                if (loaded is { SchemaVersion: 1 } && loaded.AccountId == accountId) return loaded;
                ErrorLog.Warn($"{Folder}\\{accountId:N}.json is not this account's unlock file; nothing is set up to unlock it here.");
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                ErrorLog.Warn($"{Folder}\\{accountId:N}.json could not be read ({error.GetType().Name}); nothing is set up to unlock it here.");
            }
            return new() { AccountId = accountId };
        }
    }

    /// <summary>Every account with an unlock file here.</summary>
    internal IReadOnlyList<AccountLock> All()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        return System.IO.Directory.EnumerateFiles(Directory, "*.json")
            .Select(f => Guid.TryParseExact(Path.GetFileNameWithoutExtension(f), "N", out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty).Select(Load).ToArray();
    }

    internal void Delete(Guid accountId)
    {
        lock (Gate)
        {
            var path = PathFor(accountId);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>Changes the plain choices (<see cref="AccountLock.AskOnThisPc"/>, <see cref="AccountLock.Remember"/>) and saves.</summary>
    internal AccountLock Change(Guid accountId, Func<AccountLock, AccountLock> change)
    {
        lock (Gate)
        {
            var next = change(Load(accountId)) with { AccountId = accountId };
            if (next.AskOnThisPc && next.Methods.Count == 0)
                throw new InvalidOperationException("Set a PIN, Windows Hello or a password first, so Martlet has something to ask for.");
            return Save(next);
        }
    }

    internal AccountLock SetPin(Guid accountId, string pin, byte[]? fileKey)
    {
        if (!IsPin(pin)) throw new ArgumentException("A PIN is 4 to 12 digits.", nameof(pin));
        lock (Gate)
        {
            var current = Load(accountId);
            if (current.Encrypt && fileKey is null) throw new InvalidOperationException("Unlock the account first: its files are encrypted with the PIN.");
            var secrets = Secrets(current) with
            {
                Pin = AccountProtection.Verifier(pin, iterations),
                PinKey = current.Encrypt ? AccountVault.Wrap(fileKey!, pin, iterations) : null
            };
            return Save(current with { HasPin = true }, secrets);
        }
    }

    internal AccountLock RemovePin(Guid accountId)
    {
        lock (Gate)
        {
            var current = Load(accountId);
            if (current.Encrypt) throw new InvalidOperationException("Turn off encryption first: the PIN opens your encrypted files.");
            var next = current with { HasPin = false };
            if (next.AskOnThisPc && next.Methods.Count == 0) next = next with { AskOnThisPc = false };
            return Save(next, Secrets(current) with { Pin = null, PinKey = null });
        }
    }

    /// <summary>Keeps a verifier of the account's Martlet password here, so it unlocks offline (after a Prove sign-in with that
    /// password, or a password change from this PC).</summary>
    internal AccountLock RememberPassword(Guid accountId, string password, byte[]? fileKey)
    {
        if (password is not { Length: >= MinimumPasswordLength and <= 256 })
            throw new ArgumentException($"A Martlet password has at least {MinimumPasswordLength} characters.", nameof(password));
        lock (Gate)
        {
            var current = Load(accountId);
            var secrets = Secrets(current);
            var wrap = current.Encrypt && fileKey is not null ? AccountVault.Wrap(fileKey, password, iterations) : current.Encrypt ? secrets.PasswordKey : null;
            return Save(current with { HasPassword = true }, secrets with { Password = AccountProtection.Verifier(password, iterations), PasswordKey = wrap });
        }
    }

    internal AccountLock ForgetPassword(Guid accountId)
    {
        lock (Gate)
        {
            var current = Load(accountId);
            var next = current with { HasPassword = false };
            if (next.AskOnThisPc && next.Methods.Count == 0) next = next with { AskOnThisPc = false };
            return Save(next, Secrets(current) with { Password = null, PasswordKey = null });
        }
    }

    internal AccountLock SetHello(Guid accountId, bool on)
    {
        lock (Gate)
        {
            var current = Load(accountId);
            if (on && current.Encrypt) throw new InvalidOperationException("Windows Hello can't open encrypted files; turn off encryption to use it.");
            var next = current with { Hello = on };
            if (next.AskOnThisPc && next.Methods.Count == 0) next = next with { AskOnThisPc = false };
            return Save(next);
        }
    }

    /// <summary>Turns on encryption with a new file key wrapped by the PIN (required) and the password (when given and it is
    /// the remembered one). Returns the file key; the caller encrypts the folder when the account locks.</summary>
    internal (AccountLock Lock, byte[] FileKey) TurnOnEncryption(Guid accountId, string pin, string? password)
    {
        lock (Gate)
        {
            var current = Load(accountId);
            var secrets = Secrets(current);
            if (!current.HasPin || !AccountProtection.Matches(pin, secrets.Pin)) throw new InvalidOperationException("That PIN is not this account's PIN here.");
            var withPassword = password is { Length: > 0 } && current.HasPassword && AccountProtection.Matches(password, secrets.Password);
            if (password is { Length: > 0 } && !withPassword) throw new InvalidOperationException("That password is not the one this PC remembers.");
            var key = AccountVault.NewKey();
            var next = current with { Encrypt = true, Hello = false };
            return (Save(next, secrets with
            {
                PinKey = AccountVault.Wrap(key, pin, iterations),
                PasswordKey = withPassword ? AccountVault.Wrap(key, password!, iterations) : null
            }), key);
        }
    }

    /// <summary>Forgets the file key. Decrypt the folder first.</summary>
    internal AccountLock TurnOffEncryption(Guid accountId)
    {
        lock (Gate)
        {
            var current = Load(accountId);
            return Save(current with { Encrypt = false }, Secrets(current) with { PinKey = null, PasswordKey = null });
        }
    }

    /// <summary>Checks a PIN or password (or records that Windows Hello verified the person; <paramref name="secret"/> is then
    /// null). Counts wrong tries and makes the next try wait after <see cref="FreeTries"/>.</summary>
    internal AccountUnlockResult Unlock(Guid accountId, AccountUnlockMethod method, string? secret)
    {
        lock (Gate)
        {
            var current = Load(accountId);
            var now = time.GetUtcNow();
            if (current.RetryAfter is { } wait && wait > now) return new(AccountUnlockStatus.Wait, RetryAfter: wait);
            if (method == AccountUnlockMethod.WindowsHello)
                return current.Hello && !current.Encrypt ? Succeeded(current, null) : new(AccountUnlockStatus.NotSet);
            var secrets = Secrets(current);
            var (set, verifier, wrap) = method == AccountUnlockMethod.Pin
                ? (current.HasPin, secrets.Pin, secrets.PinKey)
                : (current.HasPassword, secrets.Password, secrets.PasswordKey);
            if (!set || verifier is null) return new(AccountUnlockStatus.NotSet);
            if (!AccountProtection.Matches(secret, verifier))
            {
                var failures = current.Failures + 1;
                DateTimeOffset? retry = failures < FreeTries ? null
                    : now + TimeSpan.FromSeconds(Math.Min(LongestWait.TotalSeconds, Math.Pow(2, failures - FreeTries)));
                Save(current with { Failures = failures, RetryAfter = retry });
                return new(retry is null ? AccountUnlockStatus.Wrong : AccountUnlockStatus.Wait, RetryAfter: retry);
            }
            if (!current.Encrypt) return Succeeded(current, null);
            var key = AccountVault.Unwrap(wrap, secret);
            if (key is null)
            {
                Succeeded(current, null);
                return new(AccountUnlockStatus.NeedsPin);
            }
            return Succeeded(current, key);
        }
    }

    private AccountUnlockResult Succeeded(AccountLock current, byte[]? key)
    {
        if (current.Failures != 0 || current.RetryAfter is not null) Save(current with { Failures = 0, RetryAfter = null });
        return new(AccountUnlockStatus.Unlocked, key);
    }

    private static AccountLockSecrets Secrets(AccountLock current)
    {
        if (current.Protected is not { Length: > 0 } text) return new();
        try { return JsonSerializer.Deserialize<AccountLockSecrets>(AccountProtection.Unprotect(Convert.FromBase64String(text)), Json) ?? new(); }
        catch (Exception error) when (error is FormatException or CryptographicException or JsonException)
        {
            ErrorLog.Warn($"The unlock data of account {current.AccountId:N} on this PC can't be opened (another Windows user's, or damaged); " +
                "set its PIN and password here again.");
            return new();
        }
    }

    private AccountLock Save(AccountLock next, AccountLockSecrets? secrets = null)
    {
        var protectedText = secrets is null ? next.Protected
            : secrets.Pin is null && secrets.Password is null && secrets.PinKey is null && secrets.PasswordKey is null ? null
            : Convert.ToBase64String(AccountProtection.Protect(JsonSerializer.SerializeToUtf8Bytes(secrets, Json)));
        var saved = next with { Protected = protectedText, UpdatedAt = time.GetUtcNow() };
        System.IO.Directory.CreateDirectory(Directory);
        WritePrivate(PathFor(next.AccountId), JsonSerializer.SerializeToUtf8Bytes(saved, Json));
        return saved;
    }

    // Created with an ACL for this Windows user alone, then moved over the old file.
    private static void WritePrivate(string path, byte[] bytes)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileInfo(temporary).Create(FileMode.Create, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, security))
                stream.Write(bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
