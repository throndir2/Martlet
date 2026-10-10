using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;
using Martlet.Core.Network;

namespace Martlet.Core.Accounts;

/// <summary>The result of accepting another copy of the account directory: the joined directory and how many incoming
/// entries were refused (not signed by an active member desktop of the network).</summary>
public sealed record AccountDirectoryMerge(AccountDirectory Directory, int Rejected);

/// <summary>
/// The household's account directory (accounts.json beside network.json on desktops and beside host.json on hosts;
/// docs/ACCOUNTS.md): one last-writer-wins <see cref="Account"/> entry per account.
/// <list type="bullet">
/// <item>Each entry is stamped with a hybrid revision (<see cref="NextRevision"/>, the rule of shared settings) and signed by
/// the writing member desktop's network key (<see cref="Put"/>).</item>
/// <item><see cref="Merge"/> keeps, per account, the entry with the highest (removed, revision, writer, content): a removed
/// account never comes back. It is commutative, associative and idempotent, so every copy converges whatever order changes
/// arrive in.</item>
/// <item>A receiver (host or desktop) takes an incoming entry only when an active member desktop of its network roster
/// signed it and it keeps the account's creator (<see cref="Accept"/>, <see cref="Refusal"/>); entries it took earlier stay.</item>
/// </list>
/// Public facts only: IDs, names, roles, logins, device bindings, voice IDs, e-mail hints (hashes, never addresses) and
/// sharing defaults. JSON, snake case, schema 1.
/// </summary>
public sealed record AccountDirectory
{
    public const int SchemaVersion1 = 1;
    public const string FileName = "accounts.json";
    public const int MaximumBytes = 2 * 1024 * 1024;
    public const int MaximumAccounts = 256;
    internal const long MaximumRevision = long.MaxValue / 4;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        MaxDepth = 10
    };

    public required int SchemaVersion { get; init; }
    public required IReadOnlyList<Account> Accounts { get; init; }

    public static AccountDirectory Empty { get; } = new() { SchemaVersion = SchemaVersion1, Accounts = [] };

    /// <summary>The newest stamp in the directory (0 when empty).</summary>
    [JsonIgnore] public long Revision => Accounts.Select(a => a.Revision).DefaultIfEmpty(0).Max();

    /// <summary>The accounts that were not removed.</summary>
    [JsonIgnore] public IEnumerable<Account> Live => Accounts.Where(a => !a.Removed);

    public Account? Find(Guid id) => Accounts.FirstOrDefault(a => a.Id == id);

    /// <summary>The live account <paramref name="login"/> proves, or null. A <c>windows</c> login can belong to several accounts
    /// (each person added on one Windows login is bound to it; <see cref="AccountsWith"/> lists them); this returns the one that
    /// added it first. Should two accounts list another kind of login (linked on two computers while they were apart), the one
    /// that added it first wins too, then the lower account ID.</summary>
    public Account? FindByLogin(AccountLoginKey login) => AccountsWith(login).FirstOrDefault();

    /// <summary>The live accounts that list <paramref name="login"/>, the one that added it first first (then the lower ID).</summary>
    public IReadOnlyList<Account> AccountsWith(AccountLoginKey login)
    {
        ArgumentNullException.ThrowIfNull(login);
        return Live.Select(a => (Account: a, Login: a.Login(login))).Where(x => x.Login is not null)
            .OrderBy(x => x.Login!.AddedAt).ThenBy(x => x.Account.Key, StringComparer.Ordinal).Select(x => x.Account).ToArray();
    }

    /// <summary>The live accounts bound to <paramref name="deviceId"/>, most recently signed in there first.</summary>
    public IReadOnlyList<Account> SignedInOn(string deviceId) => Live.Where(a => a.Device(deviceId) is not null)
        .OrderByDescending(a => a.Device(deviceId)!.SignedInAt).ThenBy(a => a.Key, StringComparer.Ordinal).ToArray();

    /// <summary>A revision newer than everything in this copy and, normally, than anything written before now.</summary>
    public long NextRevision(DateTimeOffset now) => Math.Min(MaximumRevision, Math.Max(Revision + 1, now.ToUnixTimeMilliseconds()));

    /// <summary>Records <paramref name="account"/>, stamped with the next revision, <paramref name="now"/> and
    /// <paramref name="by"/>'s device ID and signed with <paramref name="by"/>'s network key. A removed account can't change. The
    /// creator stays the one this copy has; a new account takes <see cref="Account.CreatedBy"/> when set, else
    /// <paramref name="by"/>.</summary>
    public AccountDirectory Put(INetworkSigner by, Account account, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(by);
        ArgumentNullException.ThrowIfNull(account);
        var existing = Find(account.Id);
        if (existing is { Removed: true })
            throw new InvalidOperationException($"{account.Name}'s account was removed, so it can't change. Add the person again as a new account.");
        var unsigned = account.Canonical() with
        {
            CreatedBy = existing?.CreatedBy ?? (account.CreatedBy is { Length: > 0 } creator ? creator : by.DeviceId),
            Revision = NextRevision(now), UpdatedAt = now.ToUniversalTime(), UpdatedBy = by.DeviceId, Signature = ""
        };
        var signed = unsigned with { Signature = System.Buffers.Text.Base64Url.EncodeToString(by.Sign(unsigned.SigningBytes())) };
        signed.Validate();
        return Bounded(Accounts.Where(a => a.Id != signed.Id).Append(signed));
    }

    /// <summary>Removes the account <paramref name="id"/>, signed by <paramref name="by"/>: it stays as a tombstone with its ID and
    /// name, without logins, devices, voices or e-mail hints, so no older copy brings it back. <paramref name="mergedInto"/>
    /// names the account it was merged into, if any.</summary>
    public AccountDirectory Remove(INetworkSigner by, Guid id, DateTimeOffset now, Guid? mergedInto = null)
    {
        var current = Find(id) ?? throw new InvalidOperationException("That account is not in the directory.");
        if (current.Removed) return this;
        return Put(by, current with { Removed = true, MergedInto = mergedInto, Logins = [], Devices = [], Voices = [], EmailHints = [] }, now);
    }

    /// <summary>Joins two copies this computer already trusts (its own file, or entries it accepted): per account the entry with
    /// the newest (removed, revision, writer, content) wins. Signatures are not checked here; <see cref="Accept"/> checks them.</summary>
    public static AccountDirectory Merge(AccountDirectory left, AccountDirectory right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return Bounded(left.Accounts.Concat(right.Accounts).GroupBy(a => a.Id).Select(g => g.Aggregate((a, b) => Newer(a, b) ? a : b)));
    }

    /// <summary>
    /// Joins <paramref name="incoming"/> into <paramref name="current"/>, which this computer already accepted. An incoming entry
    /// is taken when <paramref name="current"/> already has exactly it, or when <see cref="Refusal"/> finds nothing against it;
    /// the others are refused (all new ones while there is no roster). Then <see cref="Merge"/> applies.
    /// </summary>
    public static AccountDirectoryMerge Accept(AccountDirectory current, AccountDirectory incoming, NetworkRoster? roster)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(incoming);
        var known = current.Accounts.Select(a => a.Content).ToHashSet(StringComparer.Ordinal);
        var taken = new List<Account>();
        var rejected = 0;
        foreach (var entry in incoming.Accounts)
        {
            if (known.Contains(entry.Content)) continue;
            if (Refusal(current.Find(entry.Id), entry, roster) is null) taken.Add(entry);
            else rejected++;
        }
        return new(taken.Count == 0 ? current : Merge(current, Empty with { Accounts = taken }), rejected);
    }

    /// <summary>
    /// The one place that decides whether a host or desktop takes an incoming entry: null when it does, else why not.
    /// <paramref name="accepted"/> is the entry for the same account this computer already has (null when none).
    /// <list type="bullet">
    /// <item><c>account.no_network</c>: this computer has no network roster to check the signer against.</item>
    /// <item><c>account.signer</c>: not signed, with the key the roster lists, by a desktop that is active in it.</item>
    /// <item><c>account.creator</c>: it changes the device that created the account.</item>
    /// </list>
    /// </summary>
    public static string? Refusal(Account? accepted, Account incoming, NetworkRoster? roster)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (roster is null) return "account.no_network";
        if (!Verify(incoming, roster)) return "account.signer";
        if (accepted is not null && accepted.Id == incoming.Id && accepted.CreatedBy != incoming.CreatedBy) return "account.creator";
        return null;
    }

    /// <summary>Whether <paramref name="entry"/> carries a valid signature by an active desktop of <paramref name="roster"/>.</summary>
    public static bool Verify(Account entry, NetworkRoster roster)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(roster);
        return roster.Desktop(entry.UpdatedBy) is { Removed: false, Key: { } key } &&
            NetworkKey.Verify(key, entry.SigningBytes(), entry.Signature);
    }

    private static bool Newer(Account a, Account b) =>
        a.Removed != b.Removed ? a.Removed
        : a.Revision != b.Revision ? a.Revision > b.Revision
        : a.UpdatedBy != b.UpdatedBy ? string.CompareOrdinal(a.UpdatedBy, b.UpdatedBy) > 0
        : string.CompareOrdinal(a.Content, b.Content) >= 0;

    // Live accounts come before removed ones, newest first, up to the limit; then sorted by ID with canonical lists, so equal
    // content always writes equal bytes.
    private static AccountDirectory Bounded(IEnumerable<Account> accounts) => new()
    {
        SchemaVersion = SchemaVersion1,
        Accounts = accounts.OrderBy(a => a.Removed).ThenByDescending(a => a.Revision).ThenBy(a => a.Key, StringComparer.Ordinal)
            .Take(MaximumAccounts).OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => a.Canonical()).ToArray()
    };

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == SchemaVersion1, "This account directory was written by a newer Martlet.", ErrorCode.UnsupportedVersion);
        ContractRules.Require(Accounts is { Count: <= MaximumAccounts } && Accounts.All(a => a is not null),
            "The account directory lists too many accounts.");
        foreach (var account in Accounts!) account.Validate();
        ContractRules.Require(Accounts.Select(a => a.Id).Distinct().Count() == Accounts.Count, "The account directory lists an account twice.");
    }

    public byte[] Write()
    {
        Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Bounded(Accounts), Json);
        ContractRules.Require(bytes.Length <= MaximumBytes, "The account directory is too large.", ErrorCode.PayloadTooLarge);
        return bytes;
    }

    public static AccountDirectory Parse(ReadOnlySpan<byte> bytes)
    {
        ContractRules.Require(bytes.Length is > 0 and <= MaximumBytes, "The account directory is empty or too large.", ErrorCode.PayloadTooLarge);
        AccountDirectory? directory;
        try { directory = JsonSerializer.Deserialize<AccountDirectory>(bytes, Json); }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException or FormatException)
        {
            throw new ContractException(ErrorCode.InvalidContract, "The account directory is malformed.");
        }
        ContractRules.Require(directory is not null, "The account directory is empty.");
        directory!.Validate();
        return Bounded(directory.Accounts);
    }

    /// <summary>Identifies the directory's content, to tell whether a copy is current.</summary>
    public string Digest() => Convert.ToHexStringLower(SHA256.HashData(Write()));

    public override string ToString() => $"Account directory r{Revision} ({Live.Count()} accounts, {Accounts.Count(a => a.Removed)} removed)";
}
