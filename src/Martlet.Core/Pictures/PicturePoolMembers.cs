using Martlet.Core.Cluster;

namespace Martlet.Core.Pictures;

/// <summary>
/// Pictures' pool members (<see cref="PoolAreas.Pictures"/>, docs/PICTURES.md#more-than-one-picture-computer): each member of
/// the list is one place a picture can be drawn, with its own settings. Martlet's pictures role on this PC's host service
/// (<see cref="PoolMemberKind.ThisPc"/>) or a paired computer (<see cref="PoolMemberKind.Computer"/>), a ComfyUI the owner runs
/// (<see cref="PoolMemberKind.Address"/>), each with a <see cref="PoolSettingKeys.Workflow"/> (z-image-turbo, checkpoint or
/// custom), its <see cref="PoolSettingKeys.Checkpoint"/> and, for a custom workflow, its <see cref="PoolSettingKeys.File"/> in
/// the data directory; or a cloud provider (<see cref="OpenRouter"/>, <see cref="NvidiaBuild"/>) with its model. The list is made
/// once from the older one-place choice (pictures.json, <see cref="Migrate"/>), and each member reads as that one-place choice
/// again (<see cref="Place"/>), so the picture makers stay the same.
/// </summary>
public static class PicturePoolMembers
{
    public const string OpenRouter = "openrouter", NvidiaBuild = "nvidia-build";
    public const string ZImageTurbo = "z-image-turbo", Checkpoint = "checkpoint", Custom = "custom";

    public static string WorkflowName(PictureWorkflow workflow) => workflow switch
    {
        PictureWorkflow.Checkpoint => Checkpoint,
        PictureWorkflow.Custom => Custom,
        _ => ZImageTurbo
    };

    public static PictureWorkflow? Workflow(string? name) => name switch
    {
        null or ZImageTurbo => PictureWorkflow.ZImageTurbo,
        Checkpoint => PictureWorkflow.Checkpoint,
        Custom => PictureWorkflow.Custom,
        _ => null
    };

    /// <summary>The member for an older one-place choice, with its workflow settings; null when pictures are off. A cloud provider
    /// carries the owner's agreement from <paramref name="at"/>: they agreed when they chose it.</summary>
    public static PoolMember? From(PicturesSettings settings, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var member = settings.Place switch
        {
            PicturePlace.Host => settings.HostId is { } host ? PoolMember.Computer(host) : PoolMember.ThisPc(),
            PicturePlace.ComfyUi => PoolMember.Service(settings.Address!),
            PicturePlace.OpenRouter => PoolMember.Cloud(OpenRouter, settings.Model).WithConsent(PoolAreas.Pictures.Id, settings.ChosenAt ?? at),
            PicturePlace.NvidiaBuild => PoolMember.Cloud(NvidiaBuild, settings.Model).WithConsent(PoolAreas.Pictures.Id, settings.ChosenAt ?? at),
            _ => null
        };
        return member is null || !settings.Comfy ? member : WithWorkflow(member, settings.Workflow, settings.Checkpoint,
            settings.Workflow == PictureWorkflow.Custom ? PicturesSettings.WorkflowFile : null);
    }

    /// <summary><paramref name="member"/> with a ComfyUI workflow, its checkpoint and its custom workflow file.</summary>
    public static PoolMember WithWorkflow(PoolMember member, PictureWorkflow workflow, string? checkpoint, string? file)
    {
        ArgumentNullException.ThrowIfNull(member);
        return member.WithSetting(PoolSettingKeys.Workflow, WorkflowName(workflow))
            .WithSetting(PoolSettingKeys.Checkpoint, workflow == PictureWorkflow.Checkpoint ? checkpoint : null)
            .WithSetting(PoolSettingKeys.File, workflow == PictureWorkflow.Custom ? file : null);
    }

    /// <summary>The first list from the older choice: the chosen place; for Martlet's pictures role, then
    /// <paramref name="others"/> (the other paired computers that run the role, in the order to try them) with the same workflow,
    /// so a picture goes to another of them when the chosen one is busy. Off: an empty list.</summary>
    public static PoolList Migrate(PicturesSettings settings, IReadOnlyList<string> others, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(others);
        var list = new PoolList { Area = PoolAreas.Pictures.Id };
        if (From(settings, at) is not { } chosen) return list;
        list = list.With(chosen);
        if (settings.Place != PicturePlace.Host) return list;
        foreach (var host in others.Where(h => h != settings.HostId))
            list = list.With(WithWorkflow(PoolMember.Computer(host), settings.Workflow, settings.Checkpoint,
                settings.Workflow == PictureWorkflow.Custom ? PicturesSettings.WorkflowFile : null));
        return list;
    }

    /// <summary>The member as a one-place choice the picture makers take: this PC's host service resolves to
    /// <paramref name="ownHost"/> (null: the first paired computer that offers the role), and a cloud member takes its own key
    /// <paramref name="credential"/> (null: Thinking's key for the same provider). Null for a member pictures can't use.</summary>
    public static PicturesSettings? Place(PoolMember member, string? ownHost, Guid? credential)
    {
        ArgumentNullException.ThrowIfNull(member);
        var workflow = Workflow(member.Setting(PoolSettingKeys.Workflow));
        if (workflow is null) return null;
        var checkpoint = workflow == PictureWorkflow.Checkpoint ? member.Setting(PoolSettingKeys.Checkpoint) : null;
        return member.Kind switch
        {
            PoolMemberKind.ThisPc => new() { Place = PicturePlace.Host, HostId = ownHost, Workflow = workflow.Value, Checkpoint = checkpoint },
            PoolMemberKind.Computer => new() { Place = PicturePlace.Host, HostId = member.HostId, Workflow = workflow.Value, Checkpoint = checkpoint },
            PoolMemberKind.Address => new() { Place = PicturePlace.ComfyUi, Address = member.Address, Workflow = workflow.Value, Checkpoint = checkpoint },
            PoolMemberKind.Cloud when member.Provider == OpenRouter =>
                new() { Place = PicturePlace.OpenRouter, ModelId = member.Model, CredentialId = credential },
            PoolMemberKind.Cloud when member.Provider == NvidiaBuild =>
                new() { Place = PicturePlace.NvidiaBuild, ModelId = member.Model, CredentialId = credential },
            _ => null
        };
    }

    /// <summary>The other paired computers that run Martlet's pictures role in the shared plan, in the order a first list takes
    /// them: this PC's own host service (<paramref name="own"/>) first, then fewest jobs first, then by ID. Never
    /// <paramref name="chosen"/>, a computer not in <paramref name="paired"/> (pass only your own hosts, not ones a friend shares)
    /// or one kept for another companion PC than <paramref name="device"/> (Devices › Sharing work).</summary>
    public static IReadOnlyList<string> Others(ClusterPlan plan, IReadOnlyCollection<string> paired, string? own, string? chosen,
        WorkSharingSettings sharing, string device)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(paired);
        ArgumentNullException.ThrowIfNull(sharing);
        return [.. plan.Nodes.Where(n => !n.Removed && n.Roles.Any(r => r.Kind == PoolAreas.Pictures.HostRole) && n.HostId != chosen &&
                paired.Contains(n.HostId) && sharing.Allows(n.HostId, device))
            .OrderBy(n => n.HostId == own ? 0 : 1)
            .ThenBy(n => plan.Assignments.Count(a => ClusterJobs.All.Contains(a.Job) && a.HostId == n.HostId))
            .ThenBy(n => n.HostId, StringComparer.Ordinal)
            .Select(n => n.HostId)];
    }

    /// <summary>The custom workflow file a member names, or the one older choices use.</summary>
    public static string WorkflowFile(PoolMember member) => member?.Setting(PoolSettingKeys.File) ?? PicturesSettings.WorkflowFile;
}
