using Martlet.Core.Cluster;
using Martlet.Core.Singing;
using Martlet.HostClient;

namespace Martlet.Desktop;

/// <summary>Companion › Singing's decisions, without WPF. The page makes one each time it draws: it reads singing.json, the music and
/// voice match lists and where Singing runs once, and says what Now and each member's line show. <see cref="SingingPage"/> draws it,
/// and <see cref="ISingingPageServices"/> is all it reads in the app.</summary>
internal sealed class SingingPageModel
{
    private readonly ISingingPageServices services;

    internal SingingPageModel(ISingingPageServices services, SingingPreferences preferences)
    {
        this.services = services;
        var directory = services.DataDirectory;
        var thisPc = services.ThisPcHost();
        var listed = directory is not null && WorkSharingRoster.Shared.Pool(directory, PoolAreas.Singing) is not null;
        var members = directory is null ? [] : SongClient.Members(directory);
        var matchOff = directory is not null && SongClient.VoiceMatchOff(directory);
        var matchers = directory is null || matchOff ? [] : SongClient.Members(directory, PoolAreas.VoiceMatch);
        Active = directory is not null && SongClient.IsSetUp(directory);
        ReadySomewhere = services.OwnHosts().Any(h => services.Offers(h, HostRoles.Singing)) || thisPc is not null && services.Offers(thisPc, HostRoles.Singing);
        // The members that sing, whose voice matches are read once from their singing service.
        Singers = [.. members.Concat(matchers).Select(m => m.Host).OfType<PairedHost>().DistinctBy(h => h.HostId).Where(h => services.Offers(h, HostRoles.Singing))];
        string Name(string hostId) => hostId == thisPc?.HostId ? "this PC" : hostId;
        string Place(PoolMember member, PairedHost? host, PoolArea area) => host is null ? SingingCloud.Describe(member, area) : Name(host.HostId);
        IReadOnlyList<string> where = listed ? [.. members.Select(m => Place(m.Member, m.Host, PoolAreas.Singing)).Distinct()]
            : preferences.Host is { } saved ? [Name(saved)] : [];
        IReadOnlyList<string> matchWhere = [.. matchers.Select(m => Place(m.Member, m.Host, PoolAreas.VoiceMatch)).Distinct()];
        var quality = members.Count > 0 ? SingingPool.Quality(members[0].Member) : SongQuality.HighQuality;
        VoiceMatch = matchers.Count == 0 ? SongVoiceMatch.SoulX : matchers[0].Host is null ? SongVoiceMatch.ElevenLabs : SingingPool.VoiceMatch(matchers[0].Member);
        NowText = Now(preferences, Active, where, listed, ReadySomewhere, matchWhere, matchOff, quality, VoiceMatch);
        NoVoice = Active && directory is not null && SongClient.SpeakingVoiceId(directory) is null && !DesktopSongSource.Fixture;
    }

    /// <summary>Whether Martlet sings: a list member can.</summary>
    internal bool Active { get; }
    /// <summary>Whether one of your computers runs Singing.</summary>
    internal bool ReadySomewhere { get; }
    /// <summary>The computers in the lists that run Singing.</summary>
    internal IReadOnlyList<PairedHost> Singers { get; }
    /// <summary>The first voice match computer's model ("In use" in the voice match models).</summary>
    internal SongVoiceMatch VoiceMatch { get; }
    /// <summary>Now's first line (SingingNow).</summary>
    internal string NowText { get; }
    /// <summary>Martlet sings, but there's no voice to sing with yet.</summary>
    internal bool NoVoice { get; }

    /// <summary>The paired computer a singing pool member sings on: this PC's own host service for this PC, else the computer
    /// (a card's computer); null when it isn't paired here (or this PC has no host service yet).</summary>
    internal PairedHost? MemberHost(PoolMember member) =>
        member.Kind == PoolMemberKind.ThisPc ? services.ThisPcHost() : services.FindHost(member.HostId) is { Shared: false } host ? host : null;

    /// <summary>The key a setup on <paramref name="member"/> is followed by: <see cref="MainWindow.SingingThisPc"/> for this PC (its own host
    /// service, or none yet), else the host ID.</summary>
    internal string SetupKey(PoolMember member) => MemberHost(member) is { } host && host.HostId != services.ThisPcHost()?.HostId
        ? host.HostId : member.Kind == PoolMemberKind.ThisPc || member.HostId == services.ThisPcHost()?.HostId ? MainWindow.SingingThisPc : member.HostId ?? MainWindow.SingingThisPc;

    /// <summary>A member's line in one of Singing's lists: whether Singing runs there (and its voice matches), is being set up,
    /// failed, or why it can't sing; then its quality (music) or its model (voice match).</summary>
    internal string? MemberStatus(PoolArea area, PoolMember member)
    {
        var key = SetupKey(member);
        if (services.PendingHost == key) return services.PendingAddOn is { } adding ? $"adding {adding.Name}..." : "setting Singing up...";
        var host = MemberHost(member);
        var parts = new List<string>();
        SongClient.SingingService? service = null;
        if (host is not null && services.Offers(host, HostRoles.Singing))
        {
            services.TryService(host.HostId, out service);
            parts.Add(service is null ? "runs Singing" : service.Has(SongVoiceMatch.VevoSing)
                ? "runs Singing with SoulX-Singer and VevoSing" : "runs Singing with SoulX-Singer");
        }
        else if (host is null)
            parts.Add(member.Kind == PoolMemberKind.ThisPc ? services.ThisPcCannotSing() ?? "doesn't run Singing yet; Settings sets it up" : "not paired with this PC");
        else
        {
            var reachable = services.Reachable(host.HostId);
            parts.Add(reachable == false ? "not reachable right now"
                : reachable == true ? "doesn't run Singing yet; Settings sets it up" : "not checked yet");
        }
        if (services.Failure(key) is { } failed) parts.Add("setup failed: " + failed.TrimEnd('.'));
        if (member.Kind == PoolMemberKind.Gpu) parts.Add("its computer picks the card");
        if (area == PoolAreas.VoiceMatch)
        {
            var model = SingingPool.VoiceMatch(member);
            parts.Add(model == SongVoiceMatch.VevoSing && service is not null && !service.Has(SongVoiceMatch.VevoSing)
                ? "VevoSing chosen, but it isn't set up here: SoulX-Singer until you add it" : SingingPool.Name(model));
        }
        else
        {
            var model = SingingPool.MusicModel(member);
            var music = model == SongMusicModel.AceStep15Xl
                ? service is not null && !service.Has(SongMusicModel.AceStep15Xl)
                    ? "ACE-Step 1.5 XL chosen, but it isn't set up here: ACE-Step 1.5 until you add it" : "ACE-Step 1.5 XL"
                : SingingPool.Quality(member) == SongQuality.Fast ? "fast" : "high quality";
            parts.Add(service is { Kinds.Count: > 0 } && !service.Singer.Splits ? music + "; makes whole songs until Martlet is updated there" : music);
        }
        return string.Join("; ", parts);
    }

    /// <summary>A cloud member's line: what it is, that each song may cost money, and its key.</summary>
    internal string? CloudStatus(PoolArea area, PoolMember member)
    {
        var known = area == PoolAreas.VoiceMatch ? SingingCloud.MatchesVoice(member) : SingingCloud.WritesMusic(member);
        if (!known) return $"Martlet can't use {SingingCloud.Name(member.Provider)} in this list; remove it";
        var parts = new List<string> { SingingCloud.Describe(member, area), "each song may cost money", KeyText(services.DataDirectory, member.Provider!) };
        if (area == PoolAreas.Singing && member.Provider != SingingCloud.ElevenLabs || member.Model != SingingCloud.DefaultModel(member.Provider!, area))
            parts.Insert(1, "model " + member.Model);
        if (!member.Consented(area.Id)) parts.Add("not agreed to: remove it and add it again");
        return string.Join("; ", parts);
    }

    /// <summary>Which key a cloud provider uses for Singing: the one saved for it, Speaking's, or none yet.</summary>
    internal static string KeyText(string? directory, string provider)
    {
        if (directory is null) return "no key yet";
        var name = SingingCloud.Name(provider);
        return SingingKeys.Load(directory).For(provider) is not null ? $"its {name} key is saved"
            : SingingCloudKeys.UsesSpeakingKey(directory, provider) ? $"it uses Speaking's {name} key"
            : $"no {name} key yet (add one in its Settings)";
    }

    /// <summary>Companion › Singing's Now line (SingingNow). <paramref name="where"/>: the computers that write the music, in
    /// order (the list's members that are on; before the list, the computer Martlet sings on). <paramref name="listed"/>: the
    /// list exists. <paramref name="matchWhere"/>: the computers that match the voice; <paramref name="matchOff"/>: the voice match
    /// list has nothing on. <paramref name="quality"/> and <paramref name="model"/>: the first computer's in each list.</summary>
    internal static string Now(SingingPreferences preferences, bool active, IReadOnlyList<string> where, bool listed, bool readySomewhere,
        IReadOnlyList<string>? matchWhere = null, bool matchOff = false, SongQuality quality = SongQuality.HighQuality,
        SongVoiceMatch model = SongVoiceMatch.SoulX)
    {
        var stays = readySomewhere ? "; Singing stays set up on your computers." : ".";
        if (listed && where.Count == 0 && !DesktopSongSource.Fixture)
            return $"Off: {OptionalExtras.OffMeans(CompanionTab.Singing)}, because no computer in the music list is on" + stays;
        if (!listed && preferences.Off) return $"Off: {OptionalExtras.OffMeans(CompanionTab.Singing)}" + stays;
        if (!active) return "Not set up yet: add one of your computers to the music list below and set Singing up there, or add a cloud provider.";
        static string Place(IReadOnlyList<string> computers) => computers.Count switch
        {
            0 => "this PC",
            1 => computers[0],
            _ => $"{computers[0]} (or the first free one of {computers.Count} computers)"
        };
        var music = DesktopSongSource.Fixture ? "this PC (FIXTURE - NOT AI)" : Place(where);
        var written = $"Martlet writes the music on {music} in {(quality == SongQuality.Fast ? "fast" : "high")} quality";
        if (matchOff) return written + ", but voice match is off, so songs keep the music model's own singer.";
        var matched = DesktopSongSource.Fixture ? music : Place(matchWhere is { Count: > 0 } ? matchWhere : where);
        return $"{written}, and matches the voice on {matched} with {SingingPool.Name(model)}.";
    }
}
