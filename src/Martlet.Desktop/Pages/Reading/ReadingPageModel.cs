using Martlet.Core.Cluster;
using Martlet.Core.Reading;
using Martlet.HostClient;

namespace Martlet.Desktop;

/// <summary>Companion › Reading's decisions, without WPF. The page makes one each time it draws: it reads the Reading list once
/// and says what Now, Read the screen, each place's state and its Reading role settings show. <see cref="ReadingPage"/> draws it,
/// and <see cref="IReadingPageServices"/> is all it reads in the app.</summary>
internal sealed partial class ReadingPageModel
{
    private readonly IReadingPageServices services;

    internal ReadingPageModel(IReadingPageServices services)
    {
        this.services = services;
        if (services.DataDirectory is { } directory) ReadingList.Ensure(directory);
        List = ReadingList.Load(services.DataDirectory);
    }

    /// <summary>The Reading list, as saved (pools-local.json, this PC's own).</summary>
    internal PoolList List { get; }

    /// <summary>Now's first line (ReadingNow).</summary>
    internal string NowText => Now(List);

    /// <summary>The newest read while watching (ReadingLast).</summary>
    internal static string LastText => ScreenReader.LastReport is { } report ? "While watching: " + report
        : "Nothing read yet: Martlet reads while you watch your screen with it (Start watching).";

    /// <summary>What Read my screen now found (ReadingTestState).</summary>
    internal string TestStateText => services.Testing ? "Reading your screen…" : services.TestState ?? "";

    /// <summary>Read the screen's choice: the index of the saved pace in <see cref="EveryNames"/>.</summary>
    internal int EveryIndex => Math.Max(0, ScreenReadPace.ChoicesMs.ToList().IndexOf(ScreenReadPace.Normalize(services.Talk.ReadEveryMs)));

    /// <summary>Read the screen's line under its choice (ReadingEveryStatus).</summary>
    internal string EveryStatus => EveryText(services.Talk.ReadEveryMs);

    internal static string Now(PoolList list)
    {
        var on = list.Members.Where(m => !m.Off).ToList();
        if (on.Count == 0) return $"Off: {OptionalExtras.OffMeans(CompanionTab.Reading)}.";
        return $"Martlet reads the text on your screen with {ReadingPool.Describe(on[0])} while it watches" +
            (on.Count > 1 ? $"; when that one is busy, the next of the {on.Count} in the list reads." : ".");
    }

    // Companion › Reading › Read the screen, in the order of ScreenReadPace.ChoicesMs.
    internal static readonly string[] EveryNames =
        ["Twice a second", "Every second", "Every 2 seconds", "Every 3 seconds (recommended)", "Every 5 seconds", "Every 10 seconds"];

    /// <summary>What Companion › Reading › Read the screen means, in words.</summary>
    internal static string EveryText(int milliseconds)
    {
        var ms = ScreenReadPace.Normalize(milliseconds);
        var pace = $"{ScreenReadPace.Describe(ms)}, Martlet reads your screen if it changed (and again after " +
            $"{ScreenReadPace.Refresh.TotalSeconds:0} seconds if it didn't)";
        return ms < ScreenReadPace.DefaultMs
            ? pace + "; reading this often uses more of the processor or graphics card that reads."
            : pace + ".";
    }

    /// <summary>Compare them: Windows OCR beside the Reading role (with the model the first computer in the list runs).</summary>
    internal IReadOnlyList<PickerOption> CompareOptions()
    {
        var first = List.Members.FirstOrDefault(m => !m.Off && !ReadingPool.UsesWindows(m));
        var firstHost = first is null ? null : ReadingPool.HostOf(first, services.ThisPcHost()?.HostId);
        return OptionalExtras.ReadingChoices(ReadingPool.Choice(List, services.ThisPcHost()?.HostId).Place, services.WindowsReads,
            firstHost is null ? null : services.Check(firstHost)?.Offers?.GetValueOrDefault(HostRoles.Ocr)).Where(o => !o.IsOff).ToList();
    }

    // ---------- the list's members ----------

    /// <summary>Where a member reads with the Reading role: its place (<see cref="MainWindow.ReadingThisPc"/> or the host ID), the
    /// paired computer (null: this PC has no host service yet, or the computer isn't paired here) and its name in words.</summary>
    internal (string Place, PairedHost? Host, string Where) RoleOf(PoolMember member) => member.Kind == PoolMemberKind.ThisPc
        ? (MainWindow.ReadingThisPc, services.ThisPcHost(), "this PC")
        : (member.HostId!, services.FindHost(member.HostId!) ?? services.FindSharedHost(member.HostId), member.HostId!);

    /// <summary>A member's state line: Windows OCR, or the model its computer's Reading role runs and whether that is the model
    /// its settings name.</summary>
    internal string? MemberStatus(PoolMember member)
    {
        if (ReadingPool.UsesWindows(member))
            return services.WindowsReads switch
            {
                true => "reads with Windows OCR on this PC's processor; nothing leaves this PC",
                false => "Windows OCR, but Windows has no text recognition language here, so the next one reads",
                _ => "reads with Windows OCR; checking it"
            };
        var (place, host, where) = RoleOf(member);
        var parts = new List<string>();
        if (member.Kind == PoolMemberKind.ThisPc) parts.Add("reads with Martlet's Reading role on this PC");
        if (services.Pending == place) parts.Add("setting up...");
        else if (host is null) parts.Add(member.Kind == PoolMemberKind.ThisPc ? "not set up yet: press Settings" : $"{where} isn't paired with this PC");
        else
        {
            var check = services.Check(host.HostId);
            if (check?.Offers?.TryGetValue(HostRoles.Ocr, out var offer) == true)
            {
                parts.Add("runs " + (Running(member, offer)?.Name ?? (string.IsNullOrEmpty(offer) ? "its reader" : offer)));
                if (OptionalExtras.ReadingModelOf(member.Setting(PoolSettingKeys.Model)) is { } wanted && wanted.Model != offer)
                    parts.Add($"its setting is {wanted.Name}: Switch in Settings");
            }
            else parts.Add(check?.Reachable == false ? "not reachable right now"
                : check?.Reachable == true ? "doesn't run Reading yet: Set up in Settings" : "not checked yet");
            if (services.Failure(place) is { } failed) parts.Add("setup failed: " + failed);
        }
        if (member.Kind == PoolMemberKind.Gpu) parts.Add("its computer picks the card");
        return string.Join("; ", parts);
    }

    /// <summary>This PC's Windows OCR line in its settings (ReadingWindowsState).</summary>
    internal string WindowsStateText => services.WindowsReads switch
    {
        true => "Windows can read text on this PC in a fraction of a second, and nothing leaves this PC.",
        false => WindowsScreenTextReader.NoLanguage,
        _ => "Checking Windows text recognition…"
    };

    /// <summary>The Reading role's model a member's computer runs: the member's saved model (with where it runs) when the computer
    /// runs that model, else the model the computer offers (its offer names only the model).</summary>
    internal static OptionalExtras.ReadingRoleModel? Running(PoolMember member, string? offer) =>
        OptionalExtras.ReadingModelOf(member.Setting(PoolSettingKeys.Model)) is { } saved && saved.Model == offer ? saved : OptionalExtras.ReadingModelOf(offer);

    /// <summary>The settings a member gets when it is added: This PC reads with Windows OCR (or with the Reading role when Windows
    /// can't read here and this PC runs it); a computer keeps the model its Reading role runs.</summary>
    internal PoolMember NewMember(PoolMember member)
    {
        if (member.Kind == PoolMemberKind.ThisPc)
            return services.WindowsReads == false && services.ThisPcHost() is { } own && services.Offers(own, HostRoles.Ocr) ? ReadingPool.ThisPcRole() : ReadingPool.Windows();
        var offer = member.HostId is { } id ? services.Check(id)?.Offers?.GetValueOrDefault(HostRoles.Ocr) : null;
        return string.IsNullOrEmpty(offer) ? member : member.WithSetting(PoolSettingKeys.Model, offer);
    }
}
