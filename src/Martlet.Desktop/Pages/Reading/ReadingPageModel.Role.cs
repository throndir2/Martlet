using Martlet.Core.Cluster;
using Martlet.HostClient;

namespace Martlet.Desktop;

/// <summary>What a member's Reading role settings show (<see cref="ReadingPageModel.Role"/>): the place and its computer, whether
/// it has an NVIDIA graphics card, the model the pills pick, where the role stands there and why it can't be set up.</summary>
internal sealed record ReadingRoleState(string Place, PairedHost? Target, string Where, bool OnThisPc, bool? Nvidia,
    OptionalExtras.ReadingRoleModel Chosen, bool Ready, bool Other, bool Pending, string? Cannot, string State, bool Warning);

internal sealed partial class ReadingPageModel
{
    /// <summary>The Reading role's settings of a member: the model the pills pick, where it stands on that computer, and whether
    /// Set up there (or Switch to the picked model) shows.</summary>
    internal ReadingRoleState Role(PoolMember member)
    {
        var (place, target, where) = RoleOf(member);
        var onThisPc = member.Kind == PoolMemberKind.ThisPc;
        var offer = target is null ? null : services.Check(target.HostId)?.Offers?.GetValueOrDefault(HostRoles.Ocr);
        var nvidia = target is not null && services.HostHasNvidia(target.HostId) is { } hostNvidia
            ? hostNvidia
            : onThisPc ? services.ThisPcHasNvidia : null;
        var running = Running(member, offer);
        var chosen = OptionalExtras.ReadingModelOf(services.PickedModel(place)) ?? OptionalExtras.ReadingModelOf(member.Setting(PoolSettingKeys.Model)) ??
            running ?? OptionalExtras.ReadingModels[0];
        if (chosen.NeedsNvidia && nvidia == false) chosen = OptionalExtras.ReadingModels[0];

        var ready = target is not null && services.Offers(target, HostRoles.Ocr);
        var runs = running?.Name ?? (string.IsNullOrEmpty(offer) ? "its reader" : offer);
        var other = ready && !string.IsNullOrEmpty(offer) && (running?.Key ?? offer) != chosen.Key;
        var pending = services.Pending == place;
        var cannot = ready || onThisPc ? null
            : target is null ? $"{where} isn't paired with this PC."
            : target.Shared ? $"A friend shares {where} with this PC; only its owner can set Reading up there."
            : services.CannotHand(where, HostRoles.Ocr, "reading");
        var failure = services.Failure(place);
        var state = pending && other ? $"Switching {where} to {chosen.Name}..."
            : ready ? $"Ready on {where} with {runs}" + (other && target is { Shared: true } ? ", and only its owner can change the model." : ".")
            : pending ? $"Setting up {chosen.Name} on {where}..."
            : cannot ?? (failure is not null ? $"Setup failed on {where}: {failure}" : $"Not set up on {where} yet.");
        var warning = cannot is not null || failure is not null && !pending && !ready;
        return new(place, target, where, onThisPc, nvidia, chosen, ready, other, pending, cannot, state, warning);
    }

    /// <summary>A model pill's label: the first model is the recommended one.</summary>
    internal static string ModelLabel(OptionalExtras.ReadingRoleModel model) =>
        model == OptionalExtras.ReadingModels[0] ? model.Name + " (recommended)" : model.Name;

    /// <summary>Why a model pill can't be chosen on <paramref name="role"/>'s computer (it has no NVIDIA graphics card), or null.</summary>
    internal static string? ModelBlocked(ReadingRoleState role, OptionalExtras.ReadingRoleModel model) =>
        model.NeedsNvidia && role.Nvidia == false ? $"Needs an NVIDIA graphics card; {role.Where} has none." : null;

    /// <summary>The chosen model's note (ReadingModelNote-&lt;place&gt;).</summary>
    internal static string ModelNote(OptionalExtras.ReadingRoleModel chosen) => chosen.Key switch
    {
        "ppocrv5-mobile" => $"PP-OCRv5 mobile runs on the processor, so it never takes the graphics card from the voice or thinking (download: {chosen.Download}). " +
            "It reads a 1920 x 1080 screenshot in about 2 s and a 4K one in about 4 s, and it read every small line on a 4K screen.",
        "ppocrv5-mobile-gpu" => $"PP-OCRv5 mobile on an NVIDIA graphics card (driver 580 or newer) is as accurate and much faster (download: {chosen.Download}). " +
            "Martlet can read your screen more often with it, and it shares the card with the voice and thinking there.",
        "ppocrv5-server" => $"PP-OCRv5 server is the most accurate model, on an NVIDIA graphics card (driver 580 or newer; download: {chosen.Download}). " +
            "It runs beside the voice and thinking; on a processor it takes about a minute a screenshot.",
        _ => $"RapidOCR (PP-OCRv4) runs on the processor and is the smallest, but it misses more small text than PP-OCRv5 (download: {chosen.Download})."
    };
}
