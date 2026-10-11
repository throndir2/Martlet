using Martlet.Core.Planning;

namespace Martlet.Desktop;

/// <summary>What the situation watch (MainWindow.Situations.cs) decided, for the request paths: the Thinking route every
/// conversation on this PC uses instead of the saved one (While gaming, Host away; LiveConversationConfiguration.From reads it)
/// and whether a game runs on this PC now. Set on the UI thread, read anywhere; reading costs nothing.</summary>
internal static class LiveSituation
{
    private static SituationOverride? current;
    private static int gaming;

    /// <summary>The situation's Thinking route, or null for the saved route.</summary>
    internal static SituationOverride? Current
    {
        get => Volatile.Read(ref current);
        set => Volatile.Write(ref current, value);
    }

    /// <summary>A game runs on this PC, and its owner said it is used for games: live work passes over this PC's own host service
    /// (its graphics card) while another computer can take it.</summary>
    internal static bool Gaming
    {
        get => Volatile.Read(ref gaming) != 0;
        set => Volatile.Write(ref gaming, value ? 1 : 0);
    }

    /// <summary>The order a request tries its computers in: as given, except that a computer that doesn't answer now
    /// (<see cref="HostPresence"/>) goes last, and while <see cref="Gaming"/> (or <paramref name="gaming"/>) this PC's own host
    /// service <paramref name="own"/> goes after the others that answer. Stable, and the same list when nothing moves.</summary>
    internal static IReadOnlyList<T> Order<T>(IReadOnlyList<T> stops, Func<T, string?> host, string? own, bool? gaming = null)
    {
        if (stops.Count < 2) return stops;
        var game = gaming ?? Gaming;
        int Rank(T stop) => host(stop) is not { } id ? 0 : HostPresence.IsOffline(id) ? 2 : game && id == own ? 1 : 0;
        return stops.Any(stop => Rank(stop) > 0) ? [.. stops.OrderBy(Rank)] : stops;
    }
}

/// <summary>The recommendation preferences the situations read on this PC (recommendation-preferences.json, read again only
/// when it changed): whether the owner said this PC is used for games and whether online services may be a backup.</summary>
internal static class SituationPreferences
{
    internal static RecommendationPreferences Load(string? dataDirectory) => dataDirectory is null ? new()
        : WorkSharingRoster.Cached(System.IO.Path.Combine(dataDirectory, RecommendationPreferences.FileName),
            () => RecommendationPreferences.Load(dataDirectory));

    /// <summary>Whether this PC (<paramref name="device"/>) is used for games: the owner's answer, else Martlet's
    /// <paramref name="guess"/> (a game library found here).</summary>
    internal static bool PlaysGames(string? dataDirectory, string device, Func<bool> guess) => Load(dataDirectory).PlaysGames(device) ?? guess();

    /// <summary>The online services preference allows the hosted backup (Only as a backup, the default, or Yes; not Never).</summary>
    internal static bool BackupAllowed(string? dataDirectory) => Load(dataDirectory).Online != OnlineServices.Never;

    /// <summary>The online services preference in words.</summary>
    internal static string Online(string? dataDirectory) => RecommendationPreferences.Words(Load(dataDirectory).Online);
}
