using Martlet.Conversation;

namespace Martlet.Desktop;

/// <summary>The other work each of your computers is kept free for, by the computer's name, so the background broker
/// (<see cref="BackgroundPlaces"/>) puts thinks on a general computer first and on these only when the general ones are busy:
/// the computer that sings (Companion › Voice › Singing). Read from this PC's files without the network, so asking costs
/// nothing. A new kind of on-demand work on a computer (image generation) adds its computer here.</summary>
internal static class BackgroundDuties
{
    internal const string Singing = "singing";

    /// <summary>Each computer's other duties ("singing"), by its name (a paired computer's host ID).</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> Of(string? dataDirectory)
    {
        var kept = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (dataDirectory is null) return kept;
        if (SingingPreferences.Load(dataDirectory).Host is { } singer) kept[singer] = [Singing];
        return kept;
    }

    /// <summary>The computer that sings, as the place a think on it would use (Deep thinking's place key for a paired computer),
    /// so a song holds it while it is made; null when no paired computer sings.</summary>
    internal static BackgroundPlace? Singer(string? dataDirectory) =>
        dataDirectory is not null && SingingPreferences.Load(dataDirectory).Host is { } host
            ? new BackgroundPlace("host:" + host, host.Length <= 80 ? host : host[..80]) { Duties = [Singing] }
            : null;
}
