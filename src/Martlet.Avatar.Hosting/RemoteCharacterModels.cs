using Martlet.Core.Contracts;

namespace Martlet.Avatar.Hosting;

/// <summary>Character models Martlet shows without keeping a copy on disk: a VRoid Hub model, named by the model path
/// <c>vroidhub:&lt;model ID&gt;</c>. VRoid Hub's rules for linked apps forbid saving its model files (with or without
/// encryption), so the bytes stay in this process's memory only, for as long as Martlet runs, and the character renderer gets
/// them through its private pipe. A model that isn't in memory is fetched through <see cref="Loader"/> (Martlet's desktop
/// downloads it with the owner's linked VRoid Hub account).</summary>
public static class RemoteCharacterModels
{
    public const string VRoidHubPrefix = "vroidhub:";
    /// <summary>How many models stay in memory at once; the one used longest ago goes first.</summary>
    public const int MaximumModels = 3;
    private static readonly object Gate = new();
    private static readonly LinkedList<(string Path, byte[] Bytes)> Models = new();
    private static readonly Dictionary<string, (Task<byte[]> Task, object Token)> Loading = new(StringComparer.Ordinal);

    /// <summary>Downloads the model at a <c>vroidhub:</c> path into memory. Null where no download is possible (the renderer
    /// process, which gets the bytes from Martlet, and MCP checks).</summary>
    public static Func<string, CancellationToken, Task<byte[]>>? Loader { get; set; }

    public static bool IsRemote(string? path) => path?.StartsWith(VRoidHubPrefix, StringComparison.Ordinal) == true;

    /// <summary>A VRoid Hub model ID: 1 to 32 digits.</summary>
    public static bool IsVRoidHubId(string? id) => id is { Length: > 0 and <= 32 } && id.All(char.IsAsciiDigit);

    public static string VRoidHubPath(string modelId)
    {
        ContractRules.Require(IsVRoidHubId(modelId), "A VRoid Hub model ID is 1 to 32 digits.");
        return VRoidHubPrefix + modelId;
    }

    /// <summary>The VRoid Hub model ID of a <c>vroidhub:</c> path, or null for any other path.</summary>
    public static string? VRoidHubId(string? path) =>
        IsRemote(path) && path![VRoidHubPrefix.Length..] is var id && IsVRoidHubId(id) ? id : null;

    public static void Validate(string path) =>
        ContractRules.Require(VRoidHubId(path) is not null, "A VRoid Hub character's path is vroidhub: and its model ID.");

    /// <summary>The file name the model's one VRM file goes by.</summary>
    public static string FileName(string path) => "vroidhub-" + VRoidHubId(path) + ".vrm";

    public static bool Has(string path)
    {
        lock (Gate) return Models.Any(m => m.Path == path);
    }

    public static void Put(string path, byte[] bytes)
    {
        Validate(path);
        ContractRules.Require(bytes.Length is > 0 and <= LocalAvatarFiles.MaximumVrmBytes,
            $"The VRoid Hub model is larger than Martlet's {Martlet.Core.Characters.CharacterModelLibrary.SizeText(LocalAvatarFiles.MaximumVrmBytes)} limit.");
        lock (Gate)
        {
            for (var node = Models.First; node is not null; node = node.Next)
                if (node.Value.Path == path) { Models.Remove(node); break; }
            Models.AddFirst((path, bytes));
            while (Models.Count > MaximumModels) Models.RemoveLast();
        }
    }

    public static void Forget(string path)
    {
        lock (Gate)
            for (var node = Models.First; node is not null; node = node.Next)
                if (node.Value.Path == path) { Models.Remove(node); return; }
    }

    /// <summary>Forgets every model (the owner unlinked VRoid Hub, or another account is in use).</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Models.Clear();
            Loading.Clear();
        }
    }

    /// <summary>The model's bytes from memory, or downloaded through <see cref="Loader"/> (one download per model at a time; a
    /// failed download is tried again by the next read). Throws <see cref="ContractException"/> when it isn't in memory and can't
    /// be downloaded.</summary>
    public static async Task<byte[]> ReadAsync(string path, CancellationToken token)
    {
        Validate(path);
        Task<byte[]> load;
        lock (Gate)
        {
            for (var node = Models.First; node is not null; node = node.Next)
                if (node.Value.Path == path)
                {
                    Models.Remove(node);
                    Models.AddFirst(node);
                    return node.Value.Bytes;
                }
            if (Loading.TryGetValue(path, out var running)) load = running.Task;
            else
            {
                var loader = Loader ?? throw new ContractException(ErrorCode.InvalidContract,
                    "This VRoid Hub character isn't loaded. Show it from Martlet with your VRoid Hub account linked (Account › Linked services).");
                var key = new object();
                load = LoadAsync(loader, path, key);
                Loading[path] = (load, key);
            }
        }
        return await load.WaitAsync(token);
    }

    private static async Task<byte[]> LoadAsync(Func<string, CancellationToken, Task<byte[]>> loader, string path, object key)
    {
        // Never finishes inside ReadAsync's lock, so the download is listed before it can end.
        await Task.Yield();
        try
        {
            var bytes = await loader(path, CancellationToken.None);
            Put(path, bytes);
            return bytes;
        }
        finally
        {
            lock (Gate)
                if (Loading.TryGetValue(path, out var current) && ReferenceEquals(current.Token, key)) Loading.Remove(path);
        }
    }
}
