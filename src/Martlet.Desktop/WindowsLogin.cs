using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

#if MARTLET_MCP
namespace Martlet.Mcp.Shared;
#else
namespace Martlet.Desktop;
#endif

/// <summary>The Windows login Martlet runs under (docs/ACCOUNTS.md, Logins): its SID (the subject of a <c>windows</c> login), its
/// kind (<see cref="Microsoft"/> account, <see cref="Work"/> or school account, or <see cref="Local"/> account), the e-mail of a
/// Microsoft or work account and the name Windows shows for it. The e-mail is a hint only and is personal data: never log it and
/// never return it from MCP (<see cref="ToString"/> gives the kind only).</summary>
internal sealed class WindowsLogin
{
    internal const string Microsoft = "microsoft", Work = "work", Local = "local";
    private const int NameDisplay = 3, NameUserPrincipal = 8;
    private static readonly Lazy<WindowsLogin> Read = new(ReadCurrent);

    /// <summary>The Windows security identifier (S-1-5-21-..., S-1-12-1-... for a Microsoft Entra account).</summary>
    internal required string Sid { get; init; }
    /// <summary><see cref="Microsoft"/>, <see cref="Work"/> or <see cref="Local"/>.</summary>
    internal required string Kind { get; init; }
    /// <summary>The Windows user name without its domain.</summary>
    internal required string UserName { get; init; }
    /// <summary>The name Windows shows for the login (its full name), else the user name.</summary>
    internal required string DisplayName { get; init; }
    /// <summary>The Microsoft or work account's e-mail, a hint only (personal data: never log it); null for a local account or
    /// when Windows doesn't tell it.</summary>
    internal string? EmailHint { get; init; }
    internal bool HasEmailHint => EmailHint is not null;

    /// <summary>The login of the Windows user this process runs as, read once. A work account's name and e-mail can need the
    /// domain, so read it off the UI thread the first time.</summary>
    internal static WindowsLogin Current => Read.Value;

    public override string ToString() => $"{Kind} Windows login" + (HasEmailHint ? " with an e-mail hint" : "");

    private static WindowsLogin ReadCurrent()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? "";
        var slash = identity.Name.IndexOf('\\');
        var domain = slash < 0 ? "" : identity.Name[..slash];
        var user = slash < 0 ? identity.Name : identity.Name[(slash + 1)..];
        if (domain.Equals("MicrosoftAccount", StringComparison.OrdinalIgnoreCase))
            return new() { Sid = sid, Kind = Microsoft, UserName = user, DisplayName = user, EmailHint = Email(user) };
        if (domain.Length == 0 || domain.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            // An account in this PC's own user list: a local account, or one Windows signs in with a Microsoft account.
            var full = FullName(user);
            var display = string.IsNullOrWhiteSpace(full) ? user : full;
            return InternetPrincipal(user) is { } principal
                ? new() { Sid = sid, Kind = Microsoft, UserName = user, DisplayName = display, EmailHint = Email(principal) }
                : new() { Sid = sid, Kind = Local, UserName = user, DisplayName = display };
        }
        // A domain account or a Microsoft Entra (AzureAD) account: a work or school account.
        var name = UserNameEx(NameDisplay);
        return new()
        {
            Sid = sid, Kind = Work, UserName = user, DisplayName = string.IsNullOrWhiteSpace(name) ? user : name,
            EmailHint = Email(UserNameEx(NameUserPrincipal)) ?? Email(EntraUserName(sid))
        };
    }

    private static string? Email(string? value) =>
        value is { Length: > 2 and <= 256 } && value.IndexOf('@') is > 0 and var at && at < value.Length - 1 && !value.Any(char.IsWhiteSpace)
            ? value : null;

    /// <summary>The Microsoft account e-mail Windows links to a local user (NetUserGetInfo level 24), or null.</summary>
    private static string? InternetPrincipal(string user)
    {
        if (NetUserGetInfo(null, user, 24, out var buffer) != 0 || buffer == 0) return null;
        try
        {
            var info = Marshal.PtrToStructure<UserInfo24>(buffer);
            if (info.InternetIdentity == 0) return null;
            var provider = Marshal.PtrToStringUni(info.ProviderName);
            return string.Equals(provider, "MicrosoftAccount", StringComparison.OrdinalIgnoreCase)
                ? Marshal.PtrToStringUni(info.PrincipalName) ?? "" : null;
        }
        finally { NetApiBufferFree(buffer); }
    }

    /// <summary>A local user's full name (NetUserGetInfo level 10), or null.</summary>
    private static string? FullName(string user)
    {
        if (NetUserGetInfo(null, user, 10, out var buffer) != 0 || buffer == 0) return null;
        try { return Marshal.PtrToStringUni(Marshal.PtrToStructure<UserInfo10>(buffer).FullName)?.Trim(); }
        finally { NetApiBufferFree(buffer); }
    }

    private static string? UserNameEx(int format)
    {
        var size = 256u;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var name = new char[size];
            if (GetUserNameEx(format, name, ref size)) return new string(name, 0, (int)size).Trim();
            if (Marshal.GetLastWin32Error() != 234) return null; // ERROR_MORE_DATA: size now holds the length needed.
        }
        return null;
    }

    /// <summary>A Microsoft Entra account's user principal name as Windows caches it for that SID, or null.</summary>
    private static string? EntraUserName(string sid)
    {
        if (sid.Length == 0) return null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\IdentityStore\Cache\{sid}\IdentityCache\{sid}");
            return key?.GetValue("UserName") as string;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UserInfo24
    {
        public int InternetIdentity;
        public uint Flags;
        public nint ProviderName;
        public nint PrincipalName;
        public nint UserSid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UserInfo10
    {
        public nint Name;
        public nint Comment;
        public nint UserComment;
        public nint FullName;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NetUserGetInfo(string? server, string user, int level, out nint buffer);

    [DllImport("netapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NetApiBufferFree(nint buffer);

    [DllImport("secur32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetUserNameExW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool GetUserNameEx(int format, [Out] char[] name, ref uint size);
}
