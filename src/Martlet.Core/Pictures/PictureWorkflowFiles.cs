using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Cluster;
using Martlet.Core.Sync;

namespace Martlet.Core.Pictures;

/// <summary>The custom ComfyUI workflows the shared Picture making list names (pictures-workflow.json and
/// pictures-workflow-&lt;key&gt;.json, <see cref="PictureMakingPoolMembers.WorkflowFile"/>), carried to the owner's other computers as
/// the household setting <see cref="SharedKey"/> beside the list, so a member with its own workflow draws the same everywhere.</summary>
public static class PictureWorkflowFiles
{
    public const string SharedKey = "pictures-workflows";
    /// <summary>At most this many workflows travel (the list holds at most <see cref="PoolSettings.MaximumMembers"/> members).</summary>
    public const int MaximumFiles = PoolSettings.MaximumMembers;

    private sealed record Document(int SchemaVersion, SortedDictionary<string, JsonObject> Files);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 64 };

    /// <summary>The workflow file names the Picture making list's members use for a custom workflow.</summary>
    public static IReadOnlyList<string> Named(string directory) => PoolSettings.LoadFor(directory, PoolAreas.PictureMaking) is { } list
        ? [.. list.Members.Where(m => PictureMakingPoolMembers.Workflow(m.Setting(PoolSettingKeys.Workflow)) == PictureWorkflow.Custom)
            .Select(PictureMakingPoolMembers.WorkflowFile).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).Take(MaximumFiles)]
        : [];

    /// <summary>The named workflows this computer has, as they travel.</summary>
    public static string Share(string directory)
    {
        var files = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var name in Named(directory))
            if (PicturesSettings.LoadWorkflow(directory, name) is { } workflow) files[name] = workflow;
        return JsonSerializer.Serialize(new Document(1, files), Json);
    }

    /// <summary>Saves the workflows another computer shared (<see cref="Share"/>'s text); other files stay. False when they were
    /// saved by a newer Martlet, can't be read, or can't be written.</summary>
    public static bool Follow(string directory, string shared)
    {
        Document? document;
        try { document = JsonSerializer.Deserialize<Document>(shared, Json); }
        catch (Exception error) when (error is JsonException or NotSupportedException) { return false; }
        if (document is not { SchemaVersion: 1, Files: { Count: <= MaximumFiles } files }) return false;
        foreach (var (name, workflow) in files)
        {
            if (workflow is null || JsonSerializer.SerializeToUtf8Bytes(workflow).Length > PicturesSettings.MaximumWorkflowBytes) return false;
            if (PicturesSettings.LoadWorkflow(directory, name)?.ToJsonString() == workflow.ToJsonString()) continue;
            try { if (!PicturesSettings.SaveWorkflow(directory, workflow, name)) return false; }
            catch (Contracts.ContractException) { return false; }
        }
        return true;
    }

    public static ISharedSection Section(string directory) => new DelegateSection(SharedKey, "Picture making workflows",
        _ =>
        {
            var names = Named(directory);
            var times = names.Select(n => Path.Combine(directory, n)).Where(File.Exists)
                .Select(p => (DateTimeOffset?)new DateTimeOffset(File.GetLastWriteTimeUtc(p), TimeSpan.Zero)).ToArray();
            return Task.FromResult<SharedLocal?>(new(Share(directory), null, times.Length == 0, times.Length == 0 ? null : times.Max()));
        },
        (setting, _) => Task.FromResult(string.Equals(setting.Value, Share(directory), StringComparison.Ordinal) || Follow(directory, setting.Value)
            ? SharedApply.Done : SharedApply.Waiting("They were saved by a newer Martlet, or couldn't be saved on this PC.")));
}
