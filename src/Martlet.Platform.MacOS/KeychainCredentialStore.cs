using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Martlet.Companion.Platform;
using Martlet.Platform.MacOS.Native;

namespace Martlet.Platform.MacOS;

/// <summary>Keys and secrets in the user's login keychain: generic passwords with service "Martlet" and account
/// "companion/&lt;name&gt;" (visible in Keychain Access). Builds without a stable signing identity may make macOS ask
/// "Martlet wants to use your confidential information" after an update; Always Allow answers it for that version.</summary>
[SupportedOSPlatform("macos")]
public sealed class KeychainCredentialStore : ICredentialStore
{
    public const string Service = "Martlet";

    public FeatureStatus Status { get; } = FeatureStatus.Yes("Keys are saved in your login keychain (Keychain Access, service \"Martlet\").");
    public bool IsPersistent => true;

    public Task<string?> GetAsync(string name, CancellationToken cancellationToken)
    {
        var status = Keychain.Read(Account(name), out var bytes);
        if (status == Keychain.ItemNotFound) return Task.FromResult<string?>(null);
        Check(status, "read");
        try { return Task.FromResult<string?>(Encoding.UTF8.GetString(bytes!)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public Task SetAsync(string name, string secret, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        var bytes = Encoding.UTF8.GetBytes(secret);
        try { Check(Keychain.Write(Account(name), bytes), "save"); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string name, CancellationToken cancellationToken)
    {
        var status = Keychain.Delete(Account(name));
        if (status != Keychain.ItemNotFound) Check(status, "delete");
        return Task.CompletedTask;
    }

    /// <summary>The keychain account for a secret name; names are short ASCII ids such as "openai-api-key".</summary>
    internal static string Account(string name)
    {
        if (name is not { Length: > 0 and <= 128 } || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/'))
            throw new ArgumentException("Secret names are 1-128 ASCII letters, digits, '-', '_', '.' or '/'.", nameof(name));
        return "companion/" + name;
    }

    internal static string Describe(int status, string action) => status switch
    {
        -25293 or -128 => $"macOS didn't let Martlet {action} the key: access to the keychain was denied or canceled.",
        -25308 => $"Martlet couldn't {action} the key: the keychain is locked and macOS can't ask you right now.",
        -25291 or -25294 => $"Martlet couldn't {action} the key: no login keychain is available.",
        _ => $"Martlet couldn't {action} the key in the keychain (error {status})."
    };

    private static void Check(int status, string action)
    {
        if (status != 0) throw new InvalidOperationException(Describe(status, action));
    }
}

[SupportedOSPlatform("macos")]
internal static unsafe class Keychain
{
    public const int ItemNotFound = -25300;
    private const string Path = "/System/Library/Frameworks/Security.framework/Security";
    private static readonly nint Lib = NativeLibrary.Load(Path);
    private static readonly nint Class = CF.Constant(Lib, "kSecClass");
    private static readonly nint GenericPassword = CF.Constant(Lib, "kSecClassGenericPassword");
    private static readonly nint AttrService = CF.Constant(Lib, "kSecAttrService");
    private static readonly nint AttrAccount = CF.Constant(Lib, "kSecAttrAccount");
    private static readonly nint AttrLabel = CF.Constant(Lib, "kSecAttrLabel");
    private static readonly nint ValueData = CF.Constant(Lib, "kSecValueData");
    private static readonly nint ReturnData = CF.Constant(Lib, "kSecReturnData");
    private static readonly nint MatchLimit = CF.Constant(Lib, "kSecMatchLimit");
    private static readonly nint MatchLimitOne = CF.Constant(Lib, "kSecMatchLimitOne");

    [DllImport(Path)] private static extern int SecItemAdd(nint attributes, nint result);
    [DllImport(Path)] private static extern int SecItemUpdate(nint query, nint attributesToUpdate);
    [DllImport(Path)] private static extern int SecItemCopyMatching(nint query, nint* result);
    [DllImport(Path)] private static extern int SecItemDelete(nint query);

    public static int Write(string account, ReadOnlySpan<byte> secret)
    {
        var query = Query(account, out var owned);
        var data = CF.Data(secret);
        var update = CF.MutableDictionary();
        try
        {
            CF.CFDictionarySetValue(update, ValueData, data);
            var status = SecItemUpdate(query, update);
            if (status != ItemNotFound) return status;
            var label = CF.String("Martlet");
            owned.Add(label);
            CF.CFDictionarySetValue(query, ValueData, data);
            CF.CFDictionarySetValue(query, AttrLabel, label);
            return SecItemAdd(query, 0);
        }
        finally
        {
            CF.CFRelease(update);
            CF.CFRelease(data);
            Release(query, owned);
        }
    }

    public static int Read(string account, out byte[]? secret)
    {
        secret = null;
        var query = Query(account, out var owned);
        nint result = 0;
        try
        {
            CF.CFDictionarySetValue(query, ReturnData, CF.BooleanTrue);
            CF.CFDictionarySetValue(query, MatchLimit, MatchLimitOne);
            var status = SecItemCopyMatching(query, &result);
            if (status != 0) return status;
            if (result == 0) return ItemNotFound;
            secret = new Span<byte>(CF.CFDataGetBytePtr(result), (int)CF.CFDataGetLength(result)).ToArray();
            return 0;
        }
        finally
        {
            if (result != 0) CF.CFRelease(result);
            Release(query, owned);
        }
    }

    public static int Delete(string account)
    {
        var query = Query(account, out var owned);
        try { return SecItemDelete(query); }
        finally { Release(query, owned); }
    }

    private static nint Query(string account, out List<nint> owned)
    {
        var service = CF.String(KeychainCredentialStore.Service);
        var name = CF.String(account);
        owned = [service, name];
        var query = CF.MutableDictionary();
        CF.CFDictionarySetValue(query, Class, GenericPassword);
        CF.CFDictionarySetValue(query, AttrService, service);
        CF.CFDictionarySetValue(query, AttrAccount, name);
        return query;
    }

    private static void Release(nint query, List<nint> owned)
    {
        CF.CFRelease(query);
        foreach (var item in owned) CF.CFRelease(item);
    }
}
