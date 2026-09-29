using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Avatars;
using Martlet.Core.Contracts;

namespace Martlet.Avatar.Hosting;

public enum AvatarLipSync { Auto, Loudness, Audio2Face }

/// <summary>Nonsecret identity of a paired Martlet host (gateway) that relays Audio2Face over pinned TLS.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AvatarRemoteHost
{
    public required string Origin { get; init; }
    public required string HostId { get; init; }
    public required string SpkiFingerprint { get; init; }
    public required string DeviceId { get; init; }
    public required string CredentialId { get; init; }

    public void Validate()
    {
        ContractRules.Require(Uri.TryCreate(Origin, UriKind.Absolute, out var origin) && origin.Scheme == "https" &&
            System.Net.IPAddress.TryParse(origin.Host.Trim('[', ']'), out _) && origin.AbsolutePath == "/" &&
            origin.Query.Length == 0 && origin.UserInfo.Length == 0, "The Martlet host address must be https://<IP>:<port>.");
        foreach (var value in new[] { HostId, DeviceId })
            ContractRules.Require(value is { Length: > 0 and <= 64 } && char.IsAsciiLetterOrDigit(value[0]) &&
                value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'), "Invalid Martlet host or device ID.");
        ContractRules.Require(SpkiFingerprint is { Length: 71 } && SpkiFingerprint.StartsWith("sha256:", StringComparison.Ordinal) &&
            SpkiFingerprint[7..].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'), "Invalid Martlet host fingerprint.");
        ContractRules.Require(CredentialId is { Length: 22 } && CredentialId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'),
            "Invalid Martlet host credential reference.");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AvatarProfile : IContract
{
    public const int MaximumBytes = 131_072;
    public const string DefaultEndpoint = "http://127.0.0.1:52000/";
    public required int Version { get; init; }
    public required Guid ProfileId { get; init; }
    public required AvatarRenderer Renderer { get; init; }
    /// <summary>Absolute local model path, or <c>builtin:Hiyori</c> for the bundled Live2D character.</summary>
    public required string ModelPath { get; init; }
    public string? SdkDirectory { get; init; }
    public required string Endpoint { get; init; }
    public required JsonElement Configuration { get; init; }
    public string? ResourceRevision { get; init; }
    /// <summary>Show the character when Martlet starts.</summary>
    public bool AutoShow { get; init; }
    /// <summary>Auto uses a detected local Audio2Face service per sentence and voice loudness otherwise.</summary>
    public AvatarLipSync LipSync { get; init; } = AvatarLipSync.Auto;
    /// <summary>Paired Martlet host that relays Audio2Face from another computer (secret kept in the OS vault).</summary>
    public AvatarRemoteHost? RemoteHost { get; init; }

    [JsonIgnore] public AvatarConfiguration Settings =>
        AvatarJson.ReadConfiguration(Encoding.UTF8.GetBytes(Configuration.GetRawText()));

    public static AvatarProfile BuiltIn(Guid profileId, string character = BundledLive2D.DefaultCharacter) => new()
    {
        Version = 1, ProfileId = profileId, Renderer = AvatarRenderer.Live2D,
        ModelPath = BundledLive2D.Prefix + character, Endpoint = DefaultEndpoint,
        Configuration = ConfigurationElement(AvatarConfiguration.Disabled)
    };

    public void Validate()
    {
        ContractRules.Require(Version == 1, "Unsupported avatar profile version.");
        ContractRules.Require(ProfileId != Guid.Empty, "Avatar configuration needs an existing profile.");
        ContractRules.Defined(Renderer);
        ContractRules.Defined(LipSync);
        if (BundledLive2D.IsBuiltIn(ModelPath))
            ContractRules.Require(Renderer == AvatarRenderer.Live2D && BundledLive2D.Characters.Contains(ModelPath[BundledLive2D.Prefix.Length..]),
                "Choose a bundled Live2D character.");
        else LocalAvatarFiles.ValidatePath(ModelPath);
        if (SdkDirectory is not null) LocalAvatarFiles.ValidatePath(SdkDirectory);
        ContractRules.Require(Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint) &&
            endpoint.Scheme == "http" && System.Net.IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var address) &&
            System.Net.IPAddress.IsLoopback(address) && endpoint.Port is >= 1 and <= 65535 &&
            endpoint.AbsolutePath == "/" && endpoint.Query.Length == 0 && endpoint.Fragment.Length == 0 &&
            endpoint.UserInfo.Length == 0, "Choose a numeric HTTP loopback root endpoint without credentials.");
        ContractRules.Require(Configuration.ValueKind == JsonValueKind.Object, "Avatar configuration must be an object.");
        _ = Settings;
        ContractRules.Require(ResourceRevision is null || ResourceRevision.Length == 64 &&
            ResourceRevision.All(Uri.IsHexDigit), "Invalid resource revision.");
        RemoteHost?.Validate();
    }

    public static JsonElement ConfigurationElement(AvatarConfiguration configuration)
    {
        using var document = JsonDocument.Parse(AvatarJson.WriteConfiguration(configuration));
        return document.RootElement.Clone();
    }
}

public sealed record AvatarProfileLoad(AvatarProfile? Profile, string? Revision);

public sealed class AvatarProfileStore
{
    public string FilePath { get; }
    public AvatarProfileStore(string dataDirectory)
    {
        LocalAvatarFiles.ValidatePath(dataDirectory);
        FilePath = Path.Combine(dataDirectory, "avatar.json");
    }

    public async Task<AvatarProfileLoad> LoadAsync(Guid profileId, CancellationToken token = default)
    {
        byte[] bytes;
        try { bytes = await LocalAvatarFiles.ReadBoundedAsync(FilePath, AvatarProfile.MaximumBytes, token); }
        catch (FileNotFoundException) { return new(null, null); }
        catch (DirectoryNotFoundException) { return new(null, null); }
        var profile = ContractJson.Read<AvatarProfile>(bytes, AvatarProfile.MaximumBytes);
        ContractRules.Require(profile.ProfileId == profileId, "Avatar document belongs to a different application profile; export it before replacing it.");
        return new(profile, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    public async Task<string> SaveAsync(AvatarProfile profile, string? expectedRevision, CancellationToken token = default)
    {
        var bytes = ContractJson.Write(profile, AvatarProfile.MaximumBytes);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        LocalAvatarFiles.CheckAncestors(FilePath);
        LocalAvatarFiles.CheckAncestors(FilePath + ".lock");
        using var writeLock = new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        var current = await LoadAsync(profile.ProfileId, token);
        ContractRules.Require(current.Revision == expectedRevision, "Avatar document changed; reload before saving.");
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, token);
                await stream.FlushAsync(token);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            if (current.Revision is null) File.Move(temporary, FilePath);
            else File.Replace(temporary, FilePath, null);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public async Task RestoreAsync(AvatarProfile candidate, string? expectedRawRevision, CancellationToken token = default)
    {
        candidate = candidate with { Configuration = AvatarProfile.ConfigurationElement(candidate.Settings with { Enabled = false }),
            ResourceRevision = null };
        var bytes = ContractJson.Write(candidate, AvatarProfile.MaximumBytes);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        LocalAvatarFiles.CheckAncestors(FilePath);
        LocalAvatarFiles.CheckAncestors(FilePath + ".lock");
        using var writeLock = new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        byte[]? prior = null;
        try { prior = await LocalAvatarFiles.ReadBoundedAsync(FilePath, AvatarProfile.MaximumBytes, token); }
        catch (FileNotFoundException) { }
        var revision = prior is null ? null : Convert.ToHexString(SHA256.HashData(prior));
        ContractRules.Require(revision == expectedRawRevision, "Avatar document changed after recovery preview.");
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, token);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            if (prior is null) File.Move(temporary, FilePath);
            else File.Replace(temporary, FilePath, FilePath + "." + Guid.NewGuid().ToString("N") + ".bak");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
