using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Accounts;

/// <summary>What an account may do in the household (docs/ACCOUNTS.md, "Roles").</summary>
public static class AccountRoles
{
    public const string Owner = "owner";
    public const string Admin = "admin";
    public const string Member = "member";

    public static bool IsRole(string? value) => value is Owner or Admin or Member;

    /// <summary>Whether <paramref name="role"/> may change household settings and paid keys, and invite and remove people.</summary>
    public static bool ManagesHousehold(string? role) => role is Owner or Admin;
}

/// <summary>The kinds of login that prove an account (docs/ACCOUNTS.md, "Logins").</summary>
public static class AccountLoginKinds
{
    /// <summary>A Windows sign-in. Provider: the device ID where it was made. Subject: the Windows SID.</summary>
    public const string Windows = "windows";
    /// <summary>A Martlet user name and password. Provider: <see cref="MartletProvider"/>. Subject: the lowercase user name.</summary>
    public const string Martlet = "martlet";
    /// <summary>An OpenID Connect provider (Google, Microsoft, Authentik and others). Provider: the household provider ID.</summary>
    public const string Oidc = "oidc";
    public const string Discord = "discord";
    public const string Steam = "steam";

    /// <summary>The provider of every <see cref="Martlet"/> login.</summary>
    public const string MartletProvider = "martlet";

    public static bool IsKind(string? value) => value is Windows or Martlet or Oidc or Discord or Steam;
}

/// <summary>How an account shares a new character by default (docs/ACCOUNTS.md, "Sharing").</summary>
public static class AccountSharingChoices
{
    public const string Private = "private";
    public const string Copy = "copy";
    public const string Together = "together";

    public static bool IsChoice(string? value) => value is Private or Copy or Together;
}

/// <summary>Which login: <c>(kind, provider, subject)</c> as the shared contract defines it. Not a secret.</summary>
public sealed record AccountLoginKey
{
    public const int MaximumSubjectLength = 256;

    public required string Kind { get; init; }
    public required string Provider { get; init; }
    public required string Subject { get; init; }

    /// <summary>The Windows login with <paramref name="sid"/> on device <paramref name="deviceId"/>.</summary>
    public static AccountLoginKey ForWindows(string deviceId, string sid) =>
        new() { Kind = AccountLoginKinds.Windows, Provider = deviceId, Subject = sid };

    /// <summary>The Martlet password login of <paramref name="userName"/> (lowercased).</summary>
    public static AccountLoginKey ForPassword(string userName) =>
        new() { Kind = AccountLoginKinds.Martlet, Provider = AccountLoginKinds.MartletProvider, Subject = userName.Trim().ToLowerInvariant() };

    /// <summary>The login <paramref name="subject"/> of the household provider <paramref name="provider"/> (oidc, discord or steam).</summary>
    public static AccountLoginKey ForProvider(string kind, string provider, string subject) =>
        new() { Kind = kind, Provider = provider, Subject = subject };

    public override string ToString() => $"{Kind}:{Provider}:{Subject}";

    internal void Validate()
    {
        ContractRules.Require(AccountLoginKinds.IsKind(Kind), "A login's kind is invalid.");
        ContractRules.Require(ContractRules.IsIdentifier(Provider), "A login's provider is invalid.");
        var subjectValid = Kind switch
        {
            AccountLoginKinds.Windows => IsSid(Subject),
            AccountLoginKinds.Martlet => Provider == AccountLoginKinds.MartletProvider && IsUserName(Subject),
            _ => Subject is { Length: > 0 and <= MaximumSubjectLength } && Account.IsText(Subject) && Subject.Trim() == Subject
        };
        ContractRules.Require(subjectValid, $"A {Kind} login's subject is invalid.");
    }

    internal static int Compare(AccountLoginKey? a, AccountLoginKey? b)
    {
        if (a is null || b is null) return a is null ? b is null ? 0 : -1 : 1;
        var kind = string.CompareOrdinal(a.Kind, b.Kind);
        if (kind != 0) return kind;
        var provider = string.CompareOrdinal(a.Provider, b.Provider);
        return provider != 0 ? provider : string.CompareOrdinal(a.Subject, b.Subject);
    }

    /// <summary>A Windows security identifier such as S-1-5-21-1004336348-1177238915-682003330-1001.</summary>
    public static bool IsSid(string? value) => value is { Length: > 4 and <= 184 } && value.StartsWith("S-1-", StringComparison.Ordinal) &&
        value[4..].Split('-').All(part => part.Length is > 0 and <= 10 && part.All(char.IsAsciiDigit));

    /// <summary>A Martlet user name as a login subject: 1-64 lowercase ASCII letters, digits, dots, underscores, hyphens or @.</summary>
    public static bool IsUserName(string? value) => value is { Length: > 0 and <= 64 } &&
        value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_' or '-' or '@');
}

/// <summary>One login of an account: which login (<see cref="Key"/>), a label to show (an e-mail address or user name; shown,
/// never trusted) and when it was added. Public facts only: never a password, verifier or token.</summary>
public sealed record AccountLogin
{
    public const int MaximumLabelLength = 128;

    public required string Kind { get; init; }
    public required string Provider { get; init; }
    public required string Subject { get; init; }
    public string? Label { get; init; }
    public required DateTimeOffset AddedAt { get; init; }

    [JsonIgnore] public AccountLoginKey Key => new() { Kind = Kind, Provider = Provider, Subject = Subject };

    public static AccountLogin For(AccountLoginKey key, string? label, DateTimeOffset addedAt)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new()
        {
            Kind = key.Kind, Provider = key.Provider, Subject = key.Subject, Label = Account.CleanText(label, MaximumLabelLength),
            AddedAt = addedAt.ToUniversalTime()
        };
    }

    internal void Validate()
    {
        Key.Validate();
        ContractRules.Require(Label is null || Label is { Length: > 0 and <= MaximumLabelLength } && Account.IsText(Label) && Label.Trim() == Label,
            "A login's label is invalid.");
        ContractRules.Require(AddedAt.Offset == TimeSpan.Zero, "A login's time must be in UTC.");
    }
}

/// <summary>A device where the account signed in: the device ID (one Windows user on one PC), the login it signed in with
/// there, when, and the attestation a host gave for it.</summary>
public sealed record AccountDevice
{
    public const int MaximumAttestationLength = 4096;

    public required string DeviceId { get; init; }
    public required AccountLoginKey Login { get; init; }
    public required DateTimeOffset SignedInAt { get; init; }

    /// <summary>A host's signed statement that the account proved itself on this device, in the text form that the account
    /// attestation (docs/ACCOUNTS.md, "Account attestation") defines; the directory does not look inside it. Null while the
    /// device has none (for example a Windows login bound on the device that made it). 1-4,096 printable ASCII characters.</summary>
    public string? Attestation { get; init; }

    public static AccountDevice For(string deviceId, AccountLoginKey login, DateTimeOffset signedInAt, string? attestation = null) => new()
    {
        DeviceId = deviceId, Login = login, SignedInAt = signedInAt.ToUniversalTime(), Attestation = attestation
    };

    internal void Validate()
    {
        ContractRules.Identifier(DeviceId);
        ContractRules.Require(Login is not null, "A device binding names no login.");
        Login!.Validate();
        ContractRules.Require(SignedInAt.Offset == TimeSpan.Zero, "A device binding's time must be in UTC.");
        ContractRules.Require(Attestation is null || Attestation is { Length: > 0 and <= MaximumAttestationLength } &&
            Attestation.All(c => c is >= ' ' and <= '~'), "A device binding's attestation is invalid.");
    }
}

/// <summary>An account's sharing defaults (docs/ACCOUNTS.md, "Sharing").</summary>
public sealed record AccountSharing
{
    /// <summary>How a new character of this account is shared: one of <see cref="AccountSharingChoices"/>.</summary>
    public string Characters { get; init; } = AccountSharingChoices.Private;

    /// <summary>"Share new memories about me": new facts about this person also go to the household space.</summary>
    public bool MemoriesAboutMe { get; init; }

    public static AccountSharing Default { get; } = new();
}

/// <summary>
/// One person in the household (docs/ACCOUNTS.md): the account ID, a display name, a role, the logins that prove it, the
/// devices where it signed in, the voices that are this person and the sharing defaults. One entry of the account directory
/// (<see cref="AccountDirectory"/>): the newest entry per account wins, stamped with a hybrid revision and signed by the
/// writing member desktop's network key. A removed account stays as a tombstone with its ID and name and never comes back.
/// Public facts only: never a password, verifier, secret or token. Change an entry with <c>with</c> or the <c>With*</c>
/// helpers; <see cref="AccountDirectory.Put"/> then stamps and signs it.
/// </summary>
public sealed record Account
{
    public const int MaximumNameLength = 64;
    public const int MaximumLogins = 16;
    public const int MaximumDevices = 64;
    public const int MaximumVoices = 16;
    public const int MaximumEmailHints = 8;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Role { get; init; }
    public IReadOnlyList<AccountLogin> Logins { get; init; } = [];
    public IReadOnlyList<AccountDevice> Devices { get; init; } = [];
    /// <summary>Voice IDs from the household's People list (32 lowercase hex digits) that are this person.</summary>
    public IReadOnlyList<string> Voices { get; init; } = [];
    /// <summary>Hashes of this person's e-mail addresses (<see cref="EmailHintFor"/>), so a Windows login with a matching
    /// Microsoft or work e-mail can ask "Continue as ...?". Never the e-mail itself.</summary>
    public IReadOnlyList<string> EmailHints { get; init; } = [];
    public AccountSharing Sharing { get; init; } = AccountSharing.Default;
    public bool Removed { get; init; }
    /// <summary>For a removed account: the account it was merged into ("Merge another account into this one").</summary>
    public Guid? MergedInto { get; init; }
    /// <summary>The device that created the account. <see cref="AccountDirectory.Put"/> keeps it on every later write, and a
    /// receiver refuses an entry that changes it (<see cref="AccountDirectory.Refusal"/>).</summary>
    public string CreatedBy { get; init; } = "";
    public long Revision { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string UpdatedBy { get; init; } = "";
    /// <summary>base64url ECDSA P-256 signature (IEEE P1363) by <see cref="UpdatedBy"/>'s network key over the entry.</summary>
    public string Signature { get; init; } = "";

    /// <summary>The account ID as 32 lowercase hex digits, as folders and memory spaces use it.</summary>
    [JsonIgnore] public string Key => Id.ToString("N");

    /// <summary>This account's memory space ("account-&lt;32 hex&gt;").</summary>
    [JsonIgnore] public string SpaceId => "account-" + Key;

    /// <summary>A new account with a new ID (unsigned until <see cref="AccountDirectory.Put"/>).</summary>
    public static Account Create(string name, string role, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(), Name = CleanText(name, MaximumNameLength) ?? "", Role = role
    };

    public AccountLogin? Login(AccountLoginKey key) => Logins.FirstOrDefault(l => l.Key == key);

    public AccountDevice? Device(string deviceId) => Devices.FirstOrDefault(d => d.DeviceId == deviceId);

    /// <summary>This account with <paramref name="login"/> added (or its label updated, keeping when it was first added).</summary>
    public Account WithLogin(AccountLogin login)
    {
        ArgumentNullException.ThrowIfNull(login);
        var existing = Login(login.Key);
        var kept = existing is null ? login : login with { AddedAt = existing.AddedAt };
        return this with { Logins = Logins.Where(l => l.Key != login.Key).Append(kept).ToArray() };
    }

    /// <summary>This account without the login <paramref name="key"/>, and without the device bindings that signed in with it.</summary>
    public Account WithoutLogin(AccountLoginKey key) => this with
    {
        Logins = Logins.Where(l => l.Key != key).ToArray(), Devices = Devices.Where(d => d.Login != key).ToArray()
    };

    /// <summary>This account bound to <paramref name="device"/> (replacing an older binding to the same device).</summary>
    public Account WithDevice(AccountDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return this with { Devices = Devices.Where(d => d.DeviceId != device.DeviceId).Append(device).ToArray() };
    }

    public Account WithoutDevice(string deviceId) => this with { Devices = Devices.Where(d => d.DeviceId != deviceId).ToArray() };

    public Account WithVoice(string voiceId) => this with { Voices = Voices.Where(v => v != voiceId).Append(voiceId).ToArray() };

    public Account WithoutVoice(string voiceId) => this with { Voices = Voices.Where(v => v != voiceId).ToArray() };

    /// <summary>This account with the hint of <paramref name="email"/> in household <paramref name="networkId"/>.</summary>
    public Account WithEmailHint(string networkId, string email)
    {
        var hint = EmailHintFor(networkId, email);
        return this with { EmailHints = EmailHints.Where(h => h != hint).Append(hint).ToArray() };
    }

    /// <summary>The e-mail hint of <paramref name="email"/> in household <paramref name="networkId"/>: lowercase hex SHA-256 of
    /// the UTF-8 text "martlet-email-hint-v1\n&lt;network ID&gt;\n&lt;trimmed, lowercase e-mail&gt;".</summary>
    public static string EmailHintFor(string networkId, string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(networkId);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes($"martlet-email-hint-v1\n{networkId}\n{email.Trim().ToLowerInvariant()}")));
    }

    /// <summary>Whether this account has the hint of <paramref name="email"/> in household <paramref name="networkId"/>.</summary>
    public bool HasEmail(string networkId, string email) => EmailHints.Contains(EmailHintFor(networkId, email), StringComparer.Ordinal);

    /// <summary>This entry with its lists in canonical order and its times in UTC.</summary>
    internal Account Canonical() => this with
    {
        Logins = Logins.Select(l => l with { AddedAt = l.AddedAt.ToUniversalTime() })
            .OrderBy(l => l.Key, Comparer<AccountLoginKey>.Create(AccountLoginKey.Compare)).ToArray(),
        Devices = Devices.Select(d => d with { SignedInAt = d.SignedInAt.ToUniversalTime() })
            .OrderBy(d => d.DeviceId, StringComparer.Ordinal).ToArray(),
        Voices = Voices.Order(StringComparer.Ordinal).ToArray(),
        EmailHints = EmailHints.Order(StringComparer.Ordinal).ToArray(),
        UpdatedAt = UpdatedAt.ToUniversalTime()
    };

    /// <summary>The bytes <see cref="UpdatedBy"/> signs: every field but the signature, each text length-prefixed, the lists in
    /// canonical order and the times as UTC ticks.</summary>
    internal byte[] SigningBytes()
    {
        var entry = Canonical();
        var text = new StringBuilder("martlet-account-v1\n");
        void Add(string? value) => (value is null ? text.Append('-') : text.Append(value.Length).Append(':').Append(value)).Append('\n');
        void Number(long value) => text.Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
        Add(entry.Id.ToString("D"));
        Add(entry.Name);
        Add(entry.Role);
        Number(entry.Removed ? 1 : 0);
        Number(entry.Revision);
        Number(entry.UpdatedAt.UtcTicks);
        Add(entry.UpdatedBy);
        Add(entry.Sharing?.Characters);
        Number(entry.Sharing?.MemoriesAboutMe == true ? 1 : 0);
        Number(entry.Logins.Count);
        foreach (var login in entry.Logins)
        {
            Add(login.Kind);
            Add(login.Provider);
            Add(login.Subject);
            Add(login.Label);
            Number(login.AddedAt.UtcTicks);
        }
        Number(entry.Devices.Count);
        foreach (var device in entry.Devices)
        {
            Add(device.DeviceId);
            Add(device.Login?.Kind);
            Add(device.Login?.Provider);
            Add(device.Login?.Subject);
            Number(device.SignedInAt.UtcTicks);
            Add(device.Attestation);
        }
        Number(entry.Voices.Count);
        foreach (var voice in entry.Voices) Add(voice);
        Number(entry.EmailHints.Count);
        foreach (var hint in entry.EmailHints) Add(hint);
        Add(entry.MergedInto?.ToString("D"));
        Add(entry.CreatedBy);
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    /// <summary>Breaks ties between two entries with the same revision and writer.</summary>
    internal string Content => Convert.ToBase64String(SigningBytes()) + "|" + Signature;

    internal void Validate()
    {
        ContractRules.Require(Id != Guid.Empty, "An account's ID is empty.");
        ContractRules.Require(IsName(Name), "An account's name is invalid.");
        ContractRules.Require(AccountRoles.IsRole(Role), "An account's role is invalid.");
        ContractRules.Require(Logins is { Count: <= MaximumLogins } && Logins.All(l => l is not null), "An account lists too many logins.");
        ContractRules.Require(Devices is { Count: <= MaximumDevices } && Devices.All(d => d is not null), "An account lists too many devices.");
        ContractRules.Require(Voices is { Count: <= MaximumVoices } && Voices.All(IsVoiceId), "An account's voices are invalid.");
        foreach (var login in Logins!) login.Validate();
        foreach (var device in Devices!) device.Validate();
        ContractRules.Require(Logins.Select(l => l.Key).Distinct().Count() == Logins.Count, "An account lists a login twice.");
        ContractRules.Require(Devices.Select(d => d.DeviceId).Distinct(StringComparer.Ordinal).Count() == Devices.Count,
            "An account lists a device twice.");
        ContractRules.Require(Voices!.Distinct(StringComparer.Ordinal).Count() == Voices.Count, "An account lists a voice twice.");
        ContractRules.Require(EmailHints is { Count: <= MaximumEmailHints } && EmailHints.All(IsSha256) &&
            EmailHints.Distinct(StringComparer.Ordinal).Count() == EmailHints.Count, "An account's e-mail hints are invalid.");
        ContractRules.Require(Sharing is not null && AccountSharingChoices.IsChoice(Sharing.Characters), "An account's sharing is invalid.");
        ContractRules.Require(!Removed || Logins.Count == 0 && Devices.Count == 0 && Voices.Count == 0 && EmailHints.Count == 0,
            "A removed account still lists logins, devices, voices or e-mail hints.");
        ContractRules.Require(MergedInto is null || Removed && MergedInto != Id && MergedInto != Guid.Empty,
            "Only a removed account can be merged into another one.");
        ContractRules.Identifier(CreatedBy);
        ContractRules.Require(Revision is > 0 and <= AccountDirectory.MaximumRevision, "An account's revision is out of range.");
        ContractRules.Require(UpdatedAt.Offset == TimeSpan.Zero, "An account's time must be in UTC.");
        ContractRules.Identifier(UpdatedBy);
        ContractRules.Require(Network.NetworkKey.TryDecode(Signature, 64, out var signature) && signature.Length == 64,
            "An account's signature is malformed.");
    }

    public static bool IsVoiceId(string? value) => value is { Length: 32 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>A display name an account may have: 1-64 characters, trimmed, no control characters.</summary>
    public static bool IsName(string? value) => value is { Length: > 0 and <= MaximumNameLength } && IsText(value) && value.Trim() == value;

    /// <summary><paramref name="value"/> without control characters, trimmed and cut to <paramref name="maximum"/> characters;
    /// null when nothing is left.</summary>
    public static string? CleanText(string? value, int maximum)
    {
        var text = new string((value ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (text.Length > maximum) text = text[..maximum].TrimEnd();
        if (text.Length > 0 && char.IsHighSurrogate(text[^1])) text = text[..^1].TrimEnd();
        return text.Length > 0 && IsText(text) ? text : null;
    }

    internal static bool IsText(string value)
    {
        if (value.Any(char.IsControl)) return false;
        try
        {
            StrictUtf8.GetByteCount(value);
            return true;
        }
        catch (EncoderFallbackException) { return false; }
    }

    public override string ToString() => $"Account {Key} ({Name}, {Role}{(Removed ? ", removed" : "")})";
}
