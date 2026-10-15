using System.Text.Json;
using Martlet.Core.Cluster;

namespace Martlet.Core.Singing;

/// <summary>What a computer's singing service says before a job goes there (its <c>status</c> operation): its state (ready,
/// loading, busy while it makes a song, or not_provisioned), how many jobs wait in its line, the voice matches set up there,
/// and the job kinds it makes (<see cref="Kinds"/>: "song", and "music" and "match" apart once its Martlet is new enough).</summary>
public sealed record SingerState(string? State, int Waiting, IReadOnlyList<SongVoiceMatch> VoiceMatches)
{
    /// <summary>The job kinds the service makes; a service that doesn't say makes whole songs only.</summary>
    public IReadOnlyList<string> Kinds { get; init; } = ["song"];

    /// <summary>The music models set up there; ACE-Step 1.5 is always set up where Singing is.</summary>
    public IReadOnlyList<SongMusicModel> MusicModels { get; init; } = [SongMusicModel.AceStep15];

    /// <summary>Whether the service makes a song's music and its voice match as two jobs (so they can run on two computers).</summary>
    public bool Splits => Kinds.Contains("music") && Kinds.Contains("match");

    /// <summary>Whether the service separates and times a song made elsewhere (a cloud provider's).</summary>
    public bool Separates => Kinds.Contains("separate");

    /// <summary>The jobs before a new one there: the one it makes now and those waiting.</summary>
    public int Ahead => (State == "busy" ? 1 : 0) + Math.Max(0, Waiting);

    /// <summary>Reads a singing service's <c>status</c> answer. SoulX-Singer is always set up where Singing is.</summary>
    public static SingerState Parse(JsonElement status)
    {
        string? state = null;
        var waiting = 0;
        List<SongVoiceMatch> matches = [SongVoiceMatch.SoulX];
        List<string> kinds = ["song"];
        List<SongMusicModel> models = [SongMusicModel.AceStep15];
        if (status.ValueKind == JsonValueKind.Object)
        {
            if (status.TryGetProperty("state", out var value) && value.ValueKind == JsonValueKind.String) state = value.GetString();
            if (status.TryGetProperty("queue", out var queue) && queue.ValueKind == JsonValueKind.Number && queue.TryGetInt32(out var count))
                waiting = Math.Max(0, count);
            if (status.TryGetProperty("voice_matches", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var item in list.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && SingingModels.Match(item.GetString()) is { } known && !matches.Contains(known))
                        matches.Add(known);
            if (status.TryGetProperty("kinds", out var made) && made.ValueKind == JsonValueKind.Array)
                foreach (var kind in made.EnumerateArray())
                    if (kind.ValueKind == JsonValueKind.String && kind.GetString() is "music" or "match" or "separate" &&
                        !kinds.Contains(kind.GetString()!))
                        kinds.Add(kind.GetString()!);
            if (status.TryGetProperty("music_models", out var music) && music.ValueKind == JsonValueKind.Array)
                foreach (var item in music.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && SingingModels.Music(item.GetString()) is { } known && !models.Contains(known))
                        models.Add(known);
        }
        return new(state, waiting, matches) { Kinds = kinds, MusicModels = models };
    }
}

/// <summary>A job made on a singing pool: its result, the computer that made it, whether it waited in that computer's line
/// because every computer was busy (<see cref="Queued"/>), and how many jobs were before it there.</summary>
public sealed record SungOn<T>(T Result, string HostId, bool Queued, int Ahead);

/// <summary>
/// Singing on two pools of the owner's own computers that run the singing role: a song's music on the singing pool
/// (<see cref="PoolAreas.Singing"/>), then its voice match on the voice match pool (<see cref="PoolAreas.VoiceMatch"/>), which
/// may be other computers. Each job goes through Martlet's queue for shared work (<see cref="WorkQueue"/>, the pool's lane,
/// background priority):
/// <list type="number">
/// <item>Each computer in order is asked for its singing status first. The first that makes nothing now and can make the job
/// takes it. While the first computer is free, nothing else is asked.</item>
/// <item>A computer that makes another job is passed over for the next. A computer that doesn't answer, doesn't sing, isn't
/// set up, or can't make the job (a voice match on a computer whose Martlet is too old to make it apart) is passed over too.</item>
/// <item>When every computer that can make it is busy, the job waits in line on the one with the fewest jobs before it (the
/// first of those in order). The singing host keeps the line, so this PC doesn't ask again and again.</item>
/// <item>A computer whose line is full (<c>singing.busy</c>), or that stops answering during the job, is passed over for the
/// next; a job that fails on the computer (<c>song.failed</c>) is not made again elsewhere.</item>
/// </list>
/// Hosts that friends share are never in the pools: the caller gives only the owner's own computers.
/// </summary>
public static class SingingPool
{
    /// <summary>The <see cref="WorkQueue"/> lane of a song's music: the singing pool area's ID.</summary>
    public static string Lane => PoolAreas.Singing.Id;

    /// <summary>The <see cref="WorkQueue"/> lane of a song's voice match: the voice match pool area's ID.</summary>
    public static string MatchLane => PoolAreas.VoiceMatch.Id;

    /// <summary>A singing pool member's quality (<see cref="PoolMember.Settings"/>): "high_quality" or "fast"; missing: high quality.</summary>
    public const string QualitySetting = "quality";

    /// <summary>A voice match pool member's model (<see cref="PoolMember.Settings"/>): an add-on's ID (<see cref="SingingModels"/>,
    /// such as "vevosing"); missing: SoulX-Singer. A music pool member's music model uses the same setting.</summary>
    public const string ModelSetting = PoolSettingKeys.Model;

    /// <summary>The quality <paramref name="member"/> writes music in: its own choice, else <see cref="SongQuality.HighQuality"/>.</summary>
    public static SongQuality Quality(PoolMember member) => member.Setting(QualitySetting) == "fast" ? SongQuality.Fast : SongQuality.HighQuality;

    /// <summary><paramref name="member"/> with <paramref name="quality"/>; high quality, the default, is saved as no setting.</summary>
    public static PoolMember WithQuality(PoolMember member, SongQuality quality) =>
        member.WithSetting(QualitySetting, quality == SongQuality.Fast ? "fast" : null);

    /// <summary>The model <paramref name="member"/> matches voices with: its own choice, else <see cref="SongVoiceMatch.SoulX"/>.</summary>
    public static SongVoiceMatch VoiceMatch(PoolMember member) =>
        SingingModels.Match(member.Setting(ModelSetting)) ?? SongVoiceMatch.SoulX;

    /// <summary><paramref name="member"/> with <paramref name="match"/>; SoulX-Singer, the default, is saved as no setting.</summary>
    public static PoolMember WithVoiceMatch(PoolMember member, SongVoiceMatch match) =>
        member.WithSetting(ModelSetting, SingingModels.AddOn(match)?.Id);

    /// <summary>The model's name in the app: "SoulX-Singer", "VevoSing", "Vevo2", "Seed-VC", "ElevenLabs Voice Changer" or "Mureka".</summary>
    public static string Name(SongVoiceMatch match) => SingingModels.Name(match);

    /// <summary>A singing pool member's music model (<see cref="PoolMember.Settings"/>, <see cref="ModelSetting"/>): an add-on's
    /// ID (such as "ace-step-v15-xl" or "heartmula"), or ACE-Step 1.5 without one.</summary>
    public static SongMusicModel MusicModel(PoolMember member) =>
        SingingModels.Music(member.Setting(ModelSetting)) ?? SongMusicModel.AceStep15;

    /// <summary><paramref name="member"/> with <paramref name="model"/>; ACE-Step 1.5, the default, is saved as no setting.</summary>
    public static PoolMember WithMusicModel(PoolMember member, SongMusicModel model) =>
        member.WithSetting(ModelSetting, SingingModels.AddOn(model)?.Id);

    /// <summary>The music model's name in the app, such as "ACE-Step 1.5", "ACE-Step 1.5 XL" or "HeartMuLa".</summary>
    public static string Name(SongMusicModel model) => SingingModels.Name(model);

    /// <summary>The music model a singing pool member uses on a computer in <paramref name="state"/>: its own model when it is set
    /// up there, else ACE-Step 1.5 (always set up where Singing is).</summary>
    public static SongMusicModel MusicWith(PoolMember member, SingerState? state) =>
        MusicModel(member) is var wanted && state?.MusicModels.Contains(wanted) == true ? wanted : SongMusicModel.AceStep15;

    /// <summary>The <paramref name="area"/> pool (default: the singing pool) of companion PC <paramref name="device"/>, each
    /// member with the paired computer it runs on (null for a cloud provider). With a pool list (<paramref name="list"/>, the
    /// area's list in pools.json): its members in the owner's order (<see cref="PoolRouting.Order"/>), this PC as its own host
    /// service (<paramref name="own"/>) and a card as its computer, each only while it is paired here (<paramref name="paired"/>,
    /// without hosts friends share), and each cloud provider the owner agreed to that <paramref name="cloud"/> says this PC can
    /// use (it has its key). An empty list, or one with no member on, is off. Without a list yet: the older choices
    /// (<see cref="Order"/>).</summary>
    public static IReadOnlyList<(PoolMember Member, string? HostId)> Members(PoolList? list, string device, string? saved, string? own,
        IReadOnlyList<string> paired, IReadOnlyList<WorkPlace> singers, PoolArea? area = null, Func<PoolMember, bool>? cloud = null)
    {
        ArgumentNullException.ThrowIfNull(paired);
        // A job's key in the queue is the member's key (a card of a computer is its own stop; a cloud provider is its own).
        if (list is not null)
            return [.. PoolRouting.Stops(area ?? PoolAreas.Singing, list, device, m =>
                m.Kind == PoolMemberKind.Cloud ? cloud?.Invoke(m) == true ? new PoolStop(m, null) { Key = m.Key } : null
                : PoolRouting.HostOf(m, own) is { } host && paired.Contains(host, StringComparer.Ordinal) ? new PoolStop(m, host) { Key = m.Key }
                : null).Select(s => (s.Member, s.HostId))];
        return [.. Order(saved, paired, singers).Select(id => (id == own ? PoolMember.ThisPc() : PoolMember.Computer(id), (string?)id))];
    }

    /// <summary>The order songs try the owner's own computers: <paramref name="saved"/> (the computer Martlet sings on,
    /// Companion › Singing) first, then those the shared plan says run Singing (<paramref name="singers"/>, fewest plan jobs
    /// first, then by ID), then the other paired computers in <paramref name="paired"/>'s order. Only computers in
    /// <paramref name="paired"/> (this PC's own pairings, without hosts friends share) are in it. No network.</summary>
    public static IReadOnlyList<string> Order(string? saved, IReadOnlyList<string> paired, IReadOnlyList<WorkPlace> singers)
    {
        ArgumentNullException.ThrowIfNull(paired);
        ArgumentNullException.ThrowIfNull(singers);
        List<string> order = [];
        void Add(string? id)
        {
            if (id is not null && paired.Contains(id, StringComparer.Ordinal) && !order.Contains(id, StringComparer.Ordinal)) order.Add(id);
        }
        Add(saved);
        foreach (var place in singers.OrderBy(p => p.Jobs).ThenBy(p => p.HostId, StringComparer.Ordinal)) Add(place.HostId);
        foreach (var id in paired) Add(id);
        return order;
    }

    /// <summary>Why <paramref name="hostId"/> can't write a song's music at all now (null <paramref name="state"/>: it doesn't
    /// offer Singing), or null when it can (now, or after the jobs in its line). A computer whose Martlet is too old to make the
    /// music apart makes the whole song there instead.</summary>
    public static SongException? MusicRefusal(string hostId, SingerState? state) => state switch
    {
        null => new(SongErrorCodes.Unavailable, $"{hostId} doesn't sing."),
        { State: "not_provisioned" } => new(SongErrorCodes.Unavailable, $"Singing isn't set up on {hostId} yet."),
        _ => null
    };

    /// <summary>Why <paramref name="hostId"/> can't match a song's voice at all now (null <paramref name="state"/>: it doesn't
    /// offer Singing), or null when it can. A computer without its member's model (such as VevoSing) matches with SoulX-Singer
    /// (<see cref="MatchWith"/>).</summary>
    public static SongException? MatchRefusal(string hostId, SingerState? state) => MusicRefusal(hostId, state) ?? (state!.Splits ? null
        : new SongException(SongErrorCodes.Unavailable, $"Singing on {hostId} can't match a voice apart yet; update Martlet there."));

    /// <summary>The model a voice match pool member uses on a computer in <paramref name="state"/>: its own model when it is set up
    /// there, else SoulX-Singer (always set up where Singing is).</summary>
    public static SongVoiceMatch MatchWith(PoolMember member, SingerState? state) =>
        VoiceMatch(member) is var wanted && state?.VoiceMatches.Contains(wanted) == true ? wanted : SongVoiceMatch.SoulX;

    /// <summary>What a singing pool does with <paramref name="hostId"/> for a job now, in words. <paramref name="match"/>: a
    /// voice match job (else the music).</summary>
    public static string Verdict(string hostId, SingerState? state, bool match = false) =>
        (match ? MatchRefusal(hostId, state) : MusicRefusal(hostId, state)) is { } refusal ? "passed over: " + refusal.Message
            : state!.Ahead > 0 ? $"busy: {state.Ahead} job{(state.Ahead == 1 ? "" : "s")} before a new one"
            : "takes a job now";

    /// <summary>The computer the next job goes to, as <see cref="RunAsync"/> chooses it from <paramref name="states"/> (null or
    /// missing: it doesn't sing or doesn't answer): the first in <paramref name="order"/> that makes nothing now, else the one
    /// with the fewest jobs before it (the first of those); null when none can make it. <paramref name="match"/>: a voice match
    /// job (else the music).</summary>
    public static (string HostId, int Ahead)? Pick(IReadOnlyList<string> order, IReadOnlyDictionary<string, SingerState?> states,
        bool match = false)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(states);
        var able = order.Select((id, index) => (id, index, state: states.GetValueOrDefault(id)))
            .Where(c => (match ? MatchRefusal(c.id, c.state) : MusicRefusal(c.id, c.state)) is null).ToArray();
        if (able.Length == 0) return null;
        var best = able.OrderBy(c => c.state!.Ahead).ThenBy(c => c.index).First();
        return (best.id, best.state!.Ahead);
    }

    /// <summary>What a failure before a job is made means: <see cref="WorkRefusal.Busy"/> for a full line
    /// (<c>singing.busy</c>), <see cref="WorkRefusal.Unavailable"/> for a computer that can't make it (not reachable, not set
    /// up, without the voice match or the voice's recording) or stopped answering, else <see cref="WorkRefusal.None"/>: a real
    /// failure, which isn't tried again elsewhere.</summary>
    public static WorkRefusal Classify(Exception error) => error switch
    {
        SongException { Code: SongErrorCodes.Busy } => WorkRefusal.Busy,
        SongException { Code: SongErrorCodes.Unavailable or SongErrorCodes.VoiceMatchUnavailable or SongErrorCodes.ModelUnavailable or
            SongErrorCodes.VoiceMissing } => WorkRefusal.Unavailable,
        SongException => WorkRefusal.None,
        HttpRequestException or IOException => WorkRefusal.Unavailable,
        _ => WorkRefusal.None
    };

    /// <summary>Makes one job on the first of <paramref name="members"/> that takes it (see the class summary) through
    /// <paramref name="queue"/> (<see cref="PoolRunner{TStop}.RunOrWaitInLineAsync"/>, every busy computer's line in turn), in
    /// <paramref name="lane"/> (<see cref="Lane"/> for the music, <see cref="MatchLane"/> for a voice match).
    /// <paramref name="look"/> reads a computer's singing status (null: it doesn't sing); <paramref name="refuse"/> says why a
    /// computer in that state can't make the job (<see cref="MusicRefusal"/>, <see cref="MatchRefusal"/>); <paramref name="sing"/>
    /// makes the job there with the state it last reported, waiting in its line when it has one. <paramref name="classify"/>
    /// says what other failures mean (a gateway's refusals); <see cref="SongException"/>s are read by <see cref="Classify"/>.
    /// <paramref name="hostOf"/> is a member's key in the queue (a pool member's <see cref="PoolMember.Key"/>);
    /// <paramref name="nameOf"/> its name in messages and <see cref="SungOn{T}.HostId"/> (the computer; default: the key). Throws
    /// the last refusal when no computer took the job.</summary>
    public static async Task<SungOn<T>> RunAsync<TTarget, T>(WorkQueue queue, string lane, IReadOnlyList<TTarget> members,
        Func<TTarget, string> hostOf, Func<TTarget, CancellationToken, Task<SingerState?>> look,
        Func<string, SingerState?, SongException?> refuse, Func<TTarget, SingerState?, CancellationToken, Task<T>> sing,
        Func<Exception, WorkRefusal>? classify, TimeProvider? clock, CancellationToken token, Func<TTarget, string>? nameOf = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(hostOf);
        ArgumentNullException.ThrowIfNull(look);
        ArgumentNullException.ThrowIfNull(refuse);
        ArgumentNullException.ThrowIfNull(sing);
        if (members.Count == 0) throw new SongException(SongErrorCodes.Unavailable, "No computer is set up for Singing.");
        var name = nameOf ?? hostOf;
        WorkRefusal Refuse(Exception error)
        {
            if (error is SongException || classify is null) return Classify(error);
            var refusal = classify(error);
            return refusal != WorkRefusal.None ? refusal : Classify(error);
        }

        var seen = new Dictionary<string, SingerState?>(StringComparer.Ordinal);
        async Task<T> Sing(TTarget member, PoolTry tried, CancellationToken t)
        {
            if (tried.Ahead is null)
            {
                // First: a computer that makes nothing now. A busy one says how many jobs are before a new one, for the line.
                var state = await look(member, t).ConfigureAwait(false);
                seen[hostOf(member)] = state;
                if (refuse(name(member), state) is { } refusal) throw refusal;
                if (state!.Ahead > 0)
                {
                    tried.Line(state.Ahead);
                    throw new SongException(SongErrorCodes.Busy, $"{name(member)} is making another song.");
                }
            }
            return await sing(member, seen.GetValueOrDefault(hostOf(member)), t).ConfigureAwait(false);
        }

        var run = await new PoolRunner<TTarget>(lane, hostOf, Refuse, WorkPriority.Background, queue, clock)
            .RunOrWaitInLineAsync(members, Sing, int.MaxValue, token).ConfigureAwait(false);
        return new(run.Result, name(run.Stop), run.Ahead is not null, run.Ahead ?? 0);
    }
}
