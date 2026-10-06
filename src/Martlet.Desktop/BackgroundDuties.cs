using Martlet.Conversation;
using Martlet.Core.Pictures;

namespace Martlet.Desktop;

/// <summary>The other work each of your computers is kept free for, by the computer's name, so the background broker
/// (<see cref="BackgroundPlaces"/>) puts thinks on a general computer first and on these only when the general ones are busy:
/// the computer that sings (Companion › Voice › Singing) and the one that draws pictures (Companion › Pictures, a paired
/// computer's pictures role). Read from this PC's files without the network, so asking costs nothing. A new kind of
/// on-demand work on a computer adds its computer here.</summary>
internal static class BackgroundDuties
{
    internal const string Singing = "singing", Pictures = "pictures";

    /// <summary>Each computer's other duties ("singing", "pictures"), by its name (a paired computer's host ID).</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> Of(string? dataDirectory)
    {
        var kept = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (dataDirectory is null) return new Dictionary<string, IReadOnlyList<string>>();
        void Add(string? host, string duty)
        {
            if (host is null) return;
            if (!kept.TryGetValue(host, out var duties)) kept[host] = duties = [];
            duties.Add(duty);
        }
        Add(SingingPreferences.Load(dataDirectory).Host, Singing);
        Add(PainterHost(dataDirectory), Pictures);
        return kept.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value, StringComparer.Ordinal);
    }

    /// <summary>The computer that sings, as the place a think on it would use (Deep thinking's place key for a paired computer),
    /// so a song holds it while it is made; null when no paired computer sings.</summary>
    internal static BackgroundPlace? Singer(string? dataDirectory) =>
        dataDirectory is null ? null : Computer(SingingPreferences.Load(dataDirectory).Host, Singing);

    /// <summary>The paired computer that draws pictures, as a place, so a picture holds it while it is drawn; null when pictures
    /// are drawn elsewhere (a ComfyUI address, a cloud provider) or not at all.</summary>
    internal static BackgroundPlace? Painter(string? dataDirectory) =>
        dataDirectory is null ? null : Computer(PainterHost(dataDirectory), Pictures);

    private static string? PainterHost(string dataDirectory) =>
        PictureClient.Settings(dataDirectory) is { Place: PicturePlace.Host, HostId: { Length: > 0 } host } ? host : null;

    private static BackgroundPlace? Computer(string? host, string duty) =>
        host is null ? null : new BackgroundPlace("host:" + host, host.Length <= 80 ? host : host[..80]) { Duties = [duty] };
}
