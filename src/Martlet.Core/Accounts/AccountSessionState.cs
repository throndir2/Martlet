using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Accounts;

/// <summary>An account made on this device that the household's account directory doesn't have yet: its name and role wait
/// here until this PC is in a Martlet network and has read the directory from a host (docs/ACCOUNTS.md, "First start").</summary>
public sealed record PendingAccount
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Role { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>An account signed in on this device with a Prove sign-in (docs/ACCOUNTS.md, "Logins"), not with this Windows login:
/// the login it proved, the host's attestation text (<see cref="AccountAttestation.ToText"/>) and when.</summary>
public sealed record AccountProof
{
    public required Guid Id { get; init; }
    public required AccountLoginKey Login { get; init; }
    public required string Attestation { get; init; }
    public required DateTimeOffset SignedInAt { get; init; }
}

/// <summary>
/// Which accounts are signed in on this device and which one is in use now: accounts\session.json in the data folder (device
/// scope, never synced). <see cref="WindowsSid"/> is the Windows login the accounts are bound to: every account in
/// <see cref="SignedIn"/> (most recently used first) unlocks with it, except the ones in <see cref="Proofs"/>, which signed in
/// with a Prove sign-in. <see cref="Pending"/> holds the accounts the household directory doesn't have yet. IDs, names, roles,
/// logins and attestations only: never a password or key. JSON, snake case, schema 1.
/// </summary>
public sealed record AccountSessionState
{
    public const int SchemaVersion1 = 1;
    public const string Folder = "accounts";
    public const string FileName = "session.json";
    public const int MaximumBytes = 64 * 1024;
    public const int MaximumAccounts = 64;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        WriteIndented = true,
        MaxDepth = 6
    };
    private static readonly object FileGate = new();

    public required int SchemaVersion { get; init; }
    public required string WindowsSid { get; init; }
    public required Guid Current { get; init; }
    public required IReadOnlyList<Guid> SignedIn { get; init; }
    public IReadOnlyList<PendingAccount> Pending { get; init; } = [];
    public IReadOnlyList<AccountProof> Proofs { get; init; } = [];

    /// <summary>accounts\session.json in <paramref name="dataDirectory"/>.</summary>
    public static string PathFor(string dataDirectory) => Path.Combine(dataDirectory, Folder, FileName);

    /// <summary>A session signed in to <paramref name="account"/> only.</summary>
    public static AccountSessionState For(string windowsSid, Guid account, PendingAccount? pending = null) => new()
    {
        SchemaVersion = SchemaVersion1, WindowsSid = windowsSid, Current = account, SignedIn = [account],
        Pending = pending is null ? [] : [pending]
    };

    public PendingAccount? PendingFor(Guid id) => Pending.FirstOrDefault(p => p.Id == id);

    /// <summary>How <paramref name="id"/> signed in here with a Prove sign-in, or null when this Windows login unlocks it.</summary>
    public AccountProof? ProofFor(Guid id) => Proofs.FirstOrDefault(p => p.Id == id);

    /// <summary>This session with <paramref name="proof"/>'s account signed in (after the current one) by its Prove sign-in.</summary>
    public AccountSessionState Add(AccountProof proof) => this with
    {
        SignedIn = [.. SignedIn.Where(id => id != proof.Id), proof.Id],
        Pending = Pending.Where(p => p.Id != proof.Id).ToArray(),
        Proofs = [.. Proofs.Where(p => p.Id != proof.Id), proof]
    };

    /// <summary>This session with <paramref name="account"/> in use, first in <see cref="SignedIn"/>.</summary>
    public AccountSessionState Use(Guid account) => this with { Current = account, SignedIn = [account, .. SignedIn.Where(id => id != account)] };

    /// <summary>This session without <paramref name="account"/> (signed out of this device; W12). It can't be the one in use.</summary>
    public AccountSessionState Without(Guid account) => account == Current
        ? throw new InvalidOperationException("The account in use can't be signed out; switch to another one first.")
        : this with
        {
            SignedIn = SignedIn.Where(id => id != account).ToArray(),
            Pending = Pending.Where(p => p.Id != account).ToArray(),
            Proofs = Proofs.Where(p => p.Id != account).ToArray()
        };

    /// <summary>This session with <paramref name="account"/> signed in (after the current one) and, when given, pending.</summary>
    public AccountSessionState Add(Guid account, PendingAccount? pending = null) => this with
    {
        SignedIn = [.. SignedIn.Where(id => id != account), account],
        Pending = pending is null ? Pending : [.. Pending.Where(p => p.Id != account), pending]
    };

    /// <summary>This session without the pending entries the directory now has.</summary>
    public AccountSessionState Written(IEnumerable<Guid> written)
    {
        var done = written.ToHashSet();
        return Pending.Any(p => done.Contains(p.Id)) ? this with { Pending = Pending.Where(p => !done.Contains(p.Id)).ToArray() } : this;
    }

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == SchemaVersion1, "This account session was written by a newer Martlet.", ErrorCode.UnsupportedVersion);
        ContractRules.Require(AccountLoginKey.IsSid(WindowsSid), "The account session's Windows login is invalid.");
        ContractRules.Require(SignedIn is { Count: > 0 and <= MaximumAccounts } && SignedIn.Distinct().Count() == SignedIn.Count &&
            !SignedIn.Contains(Guid.Empty), "The account session's signed-in accounts are invalid.");
        ContractRules.Require(SignedIn.Contains(Current), "The account in use is not signed in.");
        ContractRules.Require(Pending is { Count: <= MaximumAccounts } && Pending.All(p => p is not null && SignedIn.Contains(p.Id) &&
            Account.IsName(p.Name) && AccountRoles.IsRole(p.Role) && p.CreatedAt.Offset == TimeSpan.Zero) &&
            Pending.Select(p => p.Id).Distinct().Count() == Pending.Count, "The account session's pending accounts are invalid.");
        ContractRules.Require(Proofs is { Count: <= MaximumAccounts } && Proofs.All(p => p is not null && SignedIn.Contains(p.Id) &&
            p.Login is not null && AccountLoginKinds.IsKind(p.Login.Kind) && p.Attestation is { Length: > 0 and <= AccountAttestation.MaximumTextLength } &&
            p.SignedInAt.Offset == TimeSpan.Zero && Pending.All(q => q.Id != p.Id)) && Proofs.Select(p => p.Id).Distinct().Count() == Proofs.Count,
            "The account session's sign-ins are invalid.");
    }

    public byte[] Write()
    {
        Validate();
        return JsonSerializer.SerializeToUtf8Bytes(this, Json);
    }

    public static AccountSessionState Parse(ReadOnlySpan<byte> bytes)
    {
        ContractRules.Require(bytes.Length is > 0 and <= MaximumBytes, "The account session is empty or too large.", ErrorCode.PayloadTooLarge);
        AccountSessionState? state;
        try { state = JsonSerializer.Deserialize<AccountSessionState>(bytes, Json); }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException or FormatException)
        {
            throw new ContractException(ErrorCode.InvalidContract, "The account session is malformed.");
        }
        ContractRules.Require(state is not null, "The account session is empty.");
        state!.Validate();
        return state;
    }

    /// <summary>The session saved in <paramref name="dataDirectory"/>, or null when there is none. Throws
    /// <see cref="ContractException"/>, <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when it can't be read.</summary>
    public static AccountSessionState? Load(string dataDirectory)
    {
        lock (FileGate)
        {
            try { return Parse(File.ReadAllBytes(PathFor(dataDirectory))); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
        }
    }

    /// <summary>Saves this session in <paramref name="dataDirectory"/> (written whole, then moved over the old one).</summary>
    public void Save(string dataDirectory)
    {
        var bytes = Write();
        lock (FileGate)
        {
            var path = PathFor(dataDirectory);
            var folder = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(folder);
            var temporary = Path.Combine(folder, $"session.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temporary, bytes);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
