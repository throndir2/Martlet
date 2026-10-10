using static Martlet.Gateway.Persistence.LinuxFileSystem;
using static Martlet.Gateway.Persistence.PersistenceFailure;

namespace Martlet.Gateway.Persistence;

// Separate from the authority directory: reading configuration never creates a lock or running marker.
internal sealed class LinuxControlDirectory : IDisposable
{
    private readonly ILinuxFileSystem fs;
    private readonly List<(LinuxDescriptor Handle, LinuxFileIdentity Identity, string Name)> chain = [];
    private int DirectoryFd => chain[^1].Handle.Value;
    internal const string Config = "host.json", Approval = "service-approval.json", Machine = "machine.json";
    internal const string Staging = "service-approval.staging";
    /// <summary>Which graphics card each role runs on, for GPU priority (written by martlet-host; not part of the approved
    /// configuration).</summary>
    internal const string Gpus = "gpus.json";
    /// <summary>The shared cluster plan the gateway keeps for paired desktops (not part of the approved configuration).</summary>
    internal const string Cluster = "cluster.json", ClusterStaging = "cluster.staging";
    /// <summary>The shared voice list (voiceprints and names) the gateway keeps for paired desktops.</summary>
    internal const string Voices = "voices.json", VoicesStaging = "voices.staging";
    internal const int MaximumVoicesBytes = 1_048_576;
    /// <summary>The shared list of voices Martlet speaks with; each live voice's recording is speaking-voice-&lt;sha256&gt;.wav.</summary>
    internal const string SpeakingVoices = "speaking-voices.json", SpeakingVoicesStaging = "speaking-voices.staging";
    internal const int MaximumSpeakingVoicesBytes = 1_048_576;
    internal const string SpeakingVoiceAudioPrefix = "speaking-voice-", SpeakingVoiceAudioSuffix = ".wav";
    internal const string SpeakingVoiceAudioStaging = "speaking-voice.staging";
    internal const int MaximumSpeakingVoiceAudioBytes = 4 * 1024 * 1024;
    /// <summary>The shared list of character models; each live model's pieces are character-model-chunk-&lt;sha256&gt;.bin.</summary>
    internal const string CharacterModels = "character-models.json", CharacterModelsStaging = "character-models.staging";
    internal const int MaximumCharacterModelsBytes = 2 * 1024 * 1024;
    internal const string CharacterModelChunkPrefix = "character-model-chunk-", CharacterModelChunkSuffix = ".bin";
    internal const string CharacterModelChunkStaging = "character-model-chunk.staging";
    internal const int MaximumCharacterModelChunkBytes = 3 * 1024 * 1024;
    /// <summary>Martlet's creations; each piece of a live creation's assets is creation-chunk-&lt;sha256&gt;.bin.</summary>
    internal const string Creations = "creations.json", CreationsStaging = "creations.staging";
    internal const int MaximumCreationsBytes = Martlet.Core.Creations.CreationLibrary.MaximumBytes;
    internal const string CreationChunkPrefix = "creation-chunk-", CreationChunkSuffix = ".bin";
    internal const string CreationChunkStaging = "creation-chunk.staging";
    internal const int MaximumCreationChunkBytes = Martlet.Core.Creations.CreationLibrary.ChunkBytes;
    /// <summary>The shared Home Assistant connection, including the access token, for paired desktops.</summary>
    internal const string HomeAssistant = "home-assistant.json", HomeAssistantStaging = "home-assistant.staging";
    internal const int MaximumHomeAssistantBytes = 16 * 1024;
    /// <summary>Commands paired computers sent through this host (never their secrets).</summary>
    internal const string Commands = "commands.json", CommandsStaging = "commands.staging";
    internal const int MaximumCommandsBytes = 1_048_576;
    /// <summary>The token the gateway writes at each start; only the host computer itself can read it, so the Martlet app
    /// that presents it runs there and may take this host's commands.</summary>
    internal const string AgentToken = "agent.token", AgentTokenStaging = "agent.staging";
    /// <summary>The gateway's log: its own activity and every computer's lines paired desktops share with it.</summary>
    internal const string Logs = "logs.json", LogsStaging = "logs.staging";
    internal const int MaximumLogsBytes = 2_097_152;
    /// <summary>The Martlet network roster this host accepted (not part of the approved configuration).</summary>
    internal const string Network = "network.json", NetworkStaging = "network.staging";
    internal const string Exposure = "exposure.json", ExposureStaging = "exposure.staging";
    internal const int MaximumExposureBytes = 4_096;
    internal const int MaximumNetworkBytes = 65_536;
    /// <summary>Sign-in settings: the owner account (password verifier, authenticator secret, recovery-code verifiers),
    /// provider client secrets and the allowed identities (not part of the approved configuration).</summary>
    internal const string SignIn = "signin.json", SignInStaging = "signin.staging";
    internal const int MaximumSignInBytes = 65_536;
    /// <summary>The network's API keys (names, scopes and SHA-256 verifiers; never a usable secret).</summary>
    internal const string ApiKeys = "api-keys.json", ApiKeysStaging = "api-keys.staging";
    internal const int MaximumApiKeysBytes = 65_536;
    /// <summary>The settings the owner's computers share, including their API keys (not part of the approved configuration).</summary>
    internal const string SharedSettings = "shared-settings.json", SharedSettingsStaging = "shared-settings.staging";
    internal const int MaximumSharedSettingsBytes = Martlet.Core.Sync.SharedSettings.MaximumBytes;
    /// <summary>Everything Martlet remembers, the same on the owner's computers (not part of the approved configuration).</summary>
    internal const string Memories = "memories.json", MemoriesStaging = "memories.staging";
    internal const int MaximumMemoriesBytes = Martlet.Core.Sync.SharedMemories.MaximumBytes;
    /// <summary>The household's account directory: public facts only (not part of the approved configuration).</summary>
    internal const string Accounts = "accounts.json", AccountsStaging = "accounts.staging";
    internal const int MaximumAccountsBytes = Martlet.Core.Accounts.AccountDirectory.MaximumBytes;
    /// <summary>One memory space (every account's memories, apart): memories-&lt;space ID&gt;.json.</summary>
    internal const string MemorySpacePrefix = "memories-", MemorySpaceSuffix = ".json", MemorySpaceStaging = "memories-space.staging";
    internal uint UserId => fs.UserId;
    internal uint GroupId => fs.GroupId;

    internal LinuxControlDirectory(string configPath, ILinuxFileSystem fs)
    {
        this.fs = fs;
        if (fs.UserId == 0 || !ValidPath(configPath) || !configPath.EndsWith("/" + Config, StringComparison.Ordinal))
            throw Error(GatewayPersistenceFailure.InvalidPath);
        try
        {
            var root = new LinuxDescriptor(fs, fs.OpenRoot());
            try { chain.Add((root, fs.Stat(root.Value), "/")); }
            catch { root.Dispose(); throw; }
            var parts = configPath.Split('/').Skip(1).SkipLast(1).ToArray();
            foreach (var part in parts)
            {
                LinuxOwnedDirectory.CheckDirectory(fs, DirectoryFd, false);
                var handle = new LinuxDescriptor(fs, fs.OpenAt(DirectoryFd, part,
                    LinuxFileSystem.Directory | NoFollow | CloseOnExec, 0, NoLinks | Beneath));
                try { chain.Add((handle, fs.Stat(handle.Value), part)); }
                catch { handle.Dispose(); throw; }
            }
            Validate();
            fs.VerifyFileSystem(DirectoryFd);
        }
        catch { Dispose(); throw; }
    }

    internal static bool ValidPath(string path) =>
        path is { Length: > 1 and <= 4096 } && path[0] == '/' &&
        path.All(c => c is >= ' ' and <= '~' && c != '\\') &&
        path.Split('/').Skip(1).All(p => p is not ("" or "." or ".."));

    private void Validate()
    {
        if (chain.Count < 2) throw Error(GatewayPersistenceFailure.InvalidPath);
        for (var i = 0; i < chain.Count; i++)
        {
            var entry = chain[i];
            LinuxOwnedDirectory.CheckDirectory(fs, entry.Handle.Value, i == chain.Count - 1);
            if (!entry.Identity.SameFile(fs.Stat(entry.Handle.Value)) ||
                i > 0 && (fs.StatAt(chain[i - 1].Handle.Value, entry.Name) is not { } named ||
                    !entry.Identity.SameFile(named)))
                throw Error(GatewayPersistenceFailure.InsecureStorage);
        }
    }

    private void Check(string name, LinuxDescriptor handle, LinuxFileIdentity expected)
    {
        Validate();
        var actual = fs.Stat(handle.Value);
        LinuxOwnedDirectory.CheckFile(fs, actual, chain[^1].Identity);
        if (!actual.SameFile(expected) || fs.HasAcl(handle.Value, false) ||
            fs.StatAt(DirectoryFd, name) is not { } named || !actual.SameFile(named))
            throw Error(GatewayPersistenceFailure.InsecureStorage);
    }

    internal byte[]? Read(string name, int maximum)
    {
        if (name is not (Config or Approval or Machine or Gpus or Cluster or Voices or SpeakingVoices or CharacterModels or Creations or HomeAssistant or Logs or Network or Exposure or SignIn or Commands or AgentToken or ApiKeys or SharedSettings or Memories) &&
            name != Accounts &&
            !IsSpeakingVoiceAudio(name) && !IsCharacterModelChunk(name) && !IsCreationChunk(name) && MemorySpaceOf(name) is null) throw Error(GatewayPersistenceFailure.InvalidPath);
        Validate();
        var before = fs.StatAt(DirectoryFd, name);
        if (before is null) return null;
        LinuxOwnedDirectory.CheckFile(fs, before.Value, chain[^1].Identity);
        using var handle = new LinuxDescriptor(fs, fs.OpenAt(DirectoryFd, name,
            NoFollow | CloseOnExec | NonBlocking, 0, NoLinks | Beneath | NoMounts));
        Check(name, handle, before.Value);
        var length = fs.Stat(handle.Value).Length;
        if (length is < 1 || length > maximum) throw Error(GatewayPersistenceFailure.InvalidState);
        var bytes = new byte[(int)length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = fs.Read(handle.Value, bytes, offset, bytes.Length - offset);
            if (count <= 0 || count > bytes.Length - offset) throw Error(GatewayPersistenceFailure.StorageFailed);
            offset += count;
        }
        if (fs.Read(handle.Value, new byte[1], 0, 1) != 0 || fs.Stat(handle.Value).Length != length)
            throw Error(GatewayPersistenceFailure.InvalidState);
        Check(name, handle, before.Value);
        return bytes;
    }

    internal void WriteApproval(byte[] bytes)
    {
        if (bytes.Length is < 1 or > 8192) throw Error(GatewayPersistenceFailure.InvalidState);
        Replace(Approval, Staging, bytes, 8192);
    }

    /// <summary>Atomically replaces cluster.json (0600, service owner). A staging file left by an interrupted write is removed first.</summary>
    internal void WriteCluster(byte[] bytes)
    {
        if (bytes.Length is < 1 or > 65_536) throw Error(GatewayPersistenceFailure.InvalidState);
        Validate();
        if (fs.StatAt(DirectoryFd, ClusterStaging) is { } stale)
        {
            LinuxOwnedDirectory.CheckFile(fs, stale, chain[^1].Identity);
            fs.Unlink(DirectoryFd, ClusterStaging);
        }
        Replace(Cluster, ClusterStaging, bytes, 65_536);
    }

    /// <summary>Atomically replaces voices.json (0600, service owner). A staging file left by an interrupted write is removed first.</summary>
    internal void WriteVoices(byte[] bytes)
    {
        if (bytes.Length is < 1 or > MaximumVoicesBytes) throw Error(GatewayPersistenceFailure.InvalidState);
        Validate();
        if (fs.StatAt(DirectoryFd, VoicesStaging) is { } stale)
        {
            LinuxOwnedDirectory.CheckFile(fs, stale, chain[^1].Identity);
            fs.Unlink(DirectoryFd, VoicesStaging);
        }
        Replace(Voices, VoicesStaging, bytes, MaximumVoicesBytes);
    }

    /// <summary>Atomically replaces home-assistant.json (0600, service owner).</summary>
    internal void WriteHomeAssistant(byte[] bytes) => ReplaceRecovering(HomeAssistant, HomeAssistantStaging, bytes, MaximumHomeAssistantBytes);

    /// <summary>Atomically replaces speaking-voices.json (0600, service owner).</summary>
    internal void WriteSpeakingVoices(byte[] bytes) => ReplaceRecovering(SpeakingVoices, SpeakingVoicesStaging, bytes, MaximumSpeakingVoicesBytes);

    /// <summary>The file name of the recording with <paramref name="sha256"/> (lower-case hex).</summary>
    internal static string SpeakingVoiceAudio(string sha256) =>
        sha256 is { Length: 64 } && sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? SpeakingVoiceAudioPrefix + sha256 + SpeakingVoiceAudioSuffix
            : throw Error(GatewayPersistenceFailure.InvalidPath);

    private static bool IsSpeakingVoiceAudio(string name) =>
        name.Length == SpeakingVoiceAudioPrefix.Length + 64 + SpeakingVoiceAudioSuffix.Length &&
        name.StartsWith(SpeakingVoiceAudioPrefix, StringComparison.Ordinal) && name.EndsWith(SpeakingVoiceAudioSuffix, StringComparison.Ordinal) &&
        name[SpeakingVoiceAudioPrefix.Length..^SpeakingVoiceAudioSuffix.Length].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>Atomically writes a voice's recording (0600, service owner).</summary>
    internal void WriteSpeakingVoiceAudio(string sha256, byte[] bytes) =>
        ReplaceRecovering(SpeakingVoiceAudio(sha256), SpeakingVoiceAudioStaging, bytes, MaximumSpeakingVoiceAudioBytes);

    /// <summary>Deletes a removed voice's recording; false when there was none.</summary>
    internal bool RemoveSpeakingVoiceAudio(string sha256)
    {
        var name = SpeakingVoiceAudio(sha256);
        if (Read(name, MaximumSpeakingVoiceAudioBytes) is null) return false;
        Validate();
        fs.Unlink(DirectoryFd, name);
        fs.Flush(DirectoryFd);
        return true;
    }

    /// <summary>Atomically replaces commands.json (0600, service owner).</summary>
    internal void WriteCommands(byte[] bytes) => ReplaceRecovering(Commands, CommandsStaging, bytes, MaximumCommandsBytes);

    /// <summary>Atomically replaces character-models.json (0600, service owner).</summary>
    internal void WriteCharacterModels(byte[] bytes) => ReplaceRecovering(CharacterModels, CharacterModelsStaging, bytes, MaximumCharacterModelsBytes);

    /// <summary>The file name of the character-model piece with <paramref name="sha256"/> (lower-case hex).</summary>
    internal static string CharacterModelChunk(string sha256) =>
        sha256 is { Length: 64 } && sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? CharacterModelChunkPrefix + sha256 + CharacterModelChunkSuffix
            : throw Error(GatewayPersistenceFailure.InvalidPath);

    private static bool IsCharacterModelChunk(string name) =>
        name.Length == CharacterModelChunkPrefix.Length + 64 + CharacterModelChunkSuffix.Length &&
        name.StartsWith(CharacterModelChunkPrefix, StringComparison.Ordinal) && name.EndsWith(CharacterModelChunkSuffix, StringComparison.Ordinal) &&
        name[CharacterModelChunkPrefix.Length..^CharacterModelChunkSuffix.Length].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>Whether the character-model piece with <paramref name="sha256"/> is kept here (a regular service-owner file),
    /// without reading it.</summary>
    internal bool HasCharacterModelChunk(string sha256)
    {
        var name = CharacterModelChunk(sha256);
        Validate();
        if (fs.StatAt(DirectoryFd, name) is not { } found) return false;
        LinuxOwnedDirectory.CheckFile(fs, found, chain[^1].Identity);
        return true;
    }

    /// <summary>Atomically writes a character-model piece (0600, service owner).</summary>
    internal void WriteCharacterModelChunk(string sha256, byte[] bytes) =>
        ReplaceRecovering(CharacterModelChunk(sha256), CharacterModelChunkStaging, bytes, MaximumCharacterModelChunkBytes);

    /// <summary>Deletes a piece no live character model uses; false when there was none.</summary>
    internal bool RemoveCharacterModelChunk(string sha256)
    {
        var name = CharacterModelChunk(sha256);
        if (!HasCharacterModelChunk(sha256)) return false;
        Validate();
        fs.Unlink(DirectoryFd, name);
        fs.Flush(DirectoryFd);
        return true;
    }

    /// <summary>Atomically replaces agent.token (0600, service owner).</summary>
    internal void WriteAgentToken(byte[] bytes) => ReplaceRecovering(AgentToken, AgentTokenStaging, bytes, 128);

    /// <summary>Atomically replaces creations.json (0600, service owner).</summary>
    internal void WriteCreations(byte[] bytes) => ReplaceRecovering(Creations, CreationsStaging, bytes, MaximumCreationsBytes);

    /// <summary>The file name of the creation piece with <paramref name="sha256"/> (lower-case hex).</summary>
    internal static string CreationChunk(string sha256) =>
        sha256 is { Length: 64 } && sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? CreationChunkPrefix + sha256 + CreationChunkSuffix
            : throw Error(GatewayPersistenceFailure.InvalidPath);

    private static bool IsCreationChunk(string name) =>
        name.Length == CreationChunkPrefix.Length + 64 + CreationChunkSuffix.Length &&
        name.StartsWith(CreationChunkPrefix, StringComparison.Ordinal) && name.EndsWith(CreationChunkSuffix, StringComparison.Ordinal) &&
        name[CreationChunkPrefix.Length..^CreationChunkSuffix.Length].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>Whether the creation piece with <paramref name="sha256"/> is kept here (a regular service-owner file), without
    /// reading it.</summary>
    internal bool HasCreationChunk(string sha256)
    {
        var name = CreationChunk(sha256);
        Validate();
        if (fs.StatAt(DirectoryFd, name) is not { } found) return false;
        LinuxOwnedDirectory.CheckFile(fs, found, chain[^1].Identity);
        return true;
    }

    /// <summary>Atomically writes a creation piece (0600, service owner).</summary>
    internal void WriteCreationChunk(string sha256, byte[] bytes) =>
        ReplaceRecovering(CreationChunk(sha256), CreationChunkStaging, bytes, MaximumCreationChunkBytes);

    /// <summary>Deletes a piece no live creation uses; false when there was none.</summary>
    internal bool RemoveCreationChunk(string sha256)
    {
        var name = CreationChunk(sha256);
        if (!HasCreationChunk(sha256)) return false;
        Validate();
        fs.Unlink(DirectoryFd, name);
        fs.Flush(DirectoryFd);
        return true;
    }

    private void ReplaceRecovering(string name, string staging, byte[] bytes, int maximum)
    {
        if (bytes.Length < 1 || bytes.Length > maximum) throw Error(GatewayPersistenceFailure.InvalidState);
        Validate();
        if (fs.StatAt(DirectoryFd, staging) is { } stale)
        {
            LinuxOwnedDirectory.CheckFile(fs, stale, chain[^1].Identity);
            fs.Unlink(DirectoryFd, staging);
        }
        Replace(name, staging, bytes, maximum);
    }

    /// <summary>Atomically replaces logs.json (0600, service owner). A staging file left by an interrupted write is removed first.</summary>
    internal void WriteLogs(byte[] bytes)
    {
        if (bytes.Length is < 1 or > MaximumLogsBytes) throw Error(GatewayPersistenceFailure.InvalidState);
        Validate();
        if (fs.StatAt(DirectoryFd, LogsStaging) is { } stale)
        {
            LinuxOwnedDirectory.CheckFile(fs, stale, chain[^1].Identity);
            fs.Unlink(DirectoryFd, LogsStaging);
        }
        Replace(Logs, LogsStaging, bytes, MaximumLogsBytes);
    }

    /// <summary>Atomically replaces network.json (0600, service owner).</summary>
    internal void WriteNetwork(byte[] bytes) => ReplaceRecovering(Network, NetworkStaging, bytes, MaximumNetworkBytes);

    /// <summary>exposure.json: the owner's outside addresses and choices for reaching this host from outside home.</summary>
    internal void WriteExposure(byte[] bytes) => ReplaceRecovering(Exposure, ExposureStaging, bytes, MaximumExposureBytes);

    /// <summary>Atomically replaces signin.json (0600, service owner).</summary>
    internal void WriteSignIn(byte[] bytes) => ReplaceRecovering(SignIn, SignInStaging, bytes, MaximumSignInBytes);

    /// <summary>Atomically replaces api-keys.json (0600, service owner).</summary>
    internal void WriteApiKeys(byte[] bytes) => ReplaceRecovering(ApiKeys, ApiKeysStaging, bytes, MaximumApiKeysBytes);

    /// <summary>Atomically replaces shared-settings.json (0600, service owner).</summary>
    internal void WriteSharedSettings(byte[] bytes) => ReplaceRecovering(SharedSettings, SharedSettingsStaging, bytes, MaximumSharedSettingsBytes);

    /// <summary>Atomically replaces memories.json (0600, service owner).</summary>
    internal void WriteMemories(byte[] bytes) => ReplaceRecovering(Memories, MemoriesStaging, bytes, MaximumMemoriesBytes);

    /// <summary>Atomically replaces accounts.json (0600, service owner).</summary>
    internal void WriteAccounts(byte[] bytes) => ReplaceRecovering(Accounts, AccountsStaging, bytes, MaximumAccountsBytes);

    /// <summary>The file name of the memory space <paramref name="space"/> (a valid space ID).</summary>
    internal static string MemorySpace(string space) =>
        Martlet.Core.Sync.MemorySpaceId.IsValid(space)
            ? MemorySpacePrefix + space + MemorySpaceSuffix
            : throw Error(GatewayPersistenceFailure.InvalidPath);

    /// <summary>The space ID a memory-space file name holds; null for any other name.</summary>
    internal static string? MemorySpaceOf(string name) =>
        name.StartsWith(MemorySpacePrefix, StringComparison.Ordinal) && name.EndsWith(MemorySpaceSuffix, StringComparison.Ordinal) &&
        name.Length > MemorySpacePrefix.Length + MemorySpaceSuffix.Length &&
        name[MemorySpacePrefix.Length..^MemorySpaceSuffix.Length] is var space && Martlet.Core.Sync.MemorySpaceId.IsValid(space) ? space : null;

    /// <summary>The IDs of the memory spaces kept here.</summary>
    internal string[] ListMemorySpaces()
    {
        Validate();
        return [.. fs.Enumerate(DirectoryFd).Select(MemorySpaceOf).OfType<string>().Order(StringComparer.Ordinal)];
    }

    /// <summary>Atomically replaces one memory space's file (0600, service owner).</summary>
    internal void WriteMemorySpace(string space, byte[] bytes) =>
        ReplaceRecovering(MemorySpace(space), MemorySpaceStaging, bytes, MaximumMemoriesBytes);

    /// <summary>Removes network.json (martlet-host network-reset), so the host is in no Martlet network.</summary>
    internal bool RemoveNetwork()
    {
        if (Read(Network, MaximumNetworkBytes) is null) return false;
        Validate();
        fs.Unlink(DirectoryFd, Network);
        fs.Flush(DirectoryFd);
        return true;
    }

    private void Replace(string name, string staging, byte[] bytes, int maximum)
    {
        _ = Read(name, maximum);
        Validate();
        using (var handle = new LinuxDescriptor(fs, fs.OpenAt(DirectoryFd, staging,
            ReadWrite | Create | Exclusive | NoFollow | CloseOnExec | NonBlocking,
            0x180, NoLinks | Beneath | NoMounts)))
        {
            var initial = fs.Stat(handle.Value);
            Check(staging, handle, initial);
            var offset = 0;
            while (offset < bytes.Length)
            {
                var count = fs.Write(handle.Value, bytes, offset, bytes.Length - offset);
                if (count <= 0 || count > bytes.Length - offset) throw Error(GatewayPersistenceFailure.StorageFailed);
                offset += count;
            }
            fs.Flush(handle.Value);
            Check(staging, handle, initial);
        }
        _ = Read(name, maximum);
        Validate();
        fs.Rename(DirectoryFd, staging, name, replace: true);
        fs.Flush(DirectoryFd);
    }

    internal void RemoveApproval()
    {
        if (Read(Approval, 8192) is null) return;
        Validate();
        fs.Unlink(DirectoryFd, Approval);
        fs.Flush(DirectoryFd);
    }

    public void Dispose()
    {
        foreach (var entry in chain.AsEnumerable().Reverse()) entry.Handle.Dispose();
        chain.Clear();
    }
}
