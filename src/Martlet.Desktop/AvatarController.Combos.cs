using System.Globalization;
using Martlet.Avatar.Hosting;

namespace Martlet.Desktop;

/// <summary>The owner's emote combos on the showing character: a reply's <c>{flustered}</c> (or Try) sets off every part at once
/// and <c>{/flustered}</c> turns its lingering parts off, each part as its own tag would.</summary>
internal sealed partial class AvatarController
{
    /// <summary>Sets off the parts of the combo <c>{tag}</c> because of <paramref name="reason"/> ("a reply" or "a try"), one
    /// right after another: a part with Hold turns on and lingers (one already on stays as it is), the others show a moment.
    /// <see cref="LastAction"/> then says what each part did. Returns how many parts the model started (or already showed); 0
    /// while the character is hidden.</summary>
    internal async Task<int> PlayComboAsync(string tag, IReadOnlyList<(CharacterActionSource Source, bool Hold)> parts, string reason,
        Task? finished, CancellationToken token)
    {
        if (!IsShowing || parts.Count == 0) return 0;
        var outcomes = new List<(CharacterActionSource Source, bool Hold, bool Was, bool Started)>();
        foreach (var (source, hold) in parts)
        {
            var was = hold && Held.Holds(source.Id);
            outcomes.Add((source, hold, was, await PlayActionAsync(source, "{" + tag + "}", finished, token, hold).ConfigureAwait(false)));
        }
        // Read after all of them: a held gesture a later part replaced only showed a moment.
        bool Shows((CharacterActionSource Source, bool Hold, bool Was, bool Started) o) => o.Hold && Held.Holds(o.Source.Id);
        var on = outcomes.Where(o => !o.Was && o.Started && Shows(o)).Select(o => o.Source).ToArray();
        var kept = outcomes.Where(o => o.Was).Select(o => o.Source).ToArray();
        var played = outcomes.Where(o => !o.Was && o.Started && !Shows(o)).Select(o => o.Source).ToArray();
        var failed = outcomes.Where(o => !o.Started).Select(o => o.Source).ToArray();
        var said = new[]
        {
            on.Length > 0 ? "turned on " + Names(on) : null, kept.Length > 0 ? "kept " + Names(kept) + " on" : null,
            played.Length > 0 ? "played " + Names(played) : null, failed.Length > 0 ? "couldn't play " + Names(failed) : null
        }.OfType<string>();
        var when = DateTime.Now.ToString("T", CultureInfo.CurrentCulture);
        Volatile.Write(ref lastAction, $"Combo {{{tag}}} for {reason} at {when}: {string.Join("; ", said)}.");
        ErrorLog.Info($"Character combo {{{tag}}} for {reason}: {on.Length} turned on, {kept.Length} kept on, {played.Length} played, " +
            $"{failed.Length} failed.");
        ActionPlayed?.Invoke();
        return outcomes.Count(o => o.Started);
    }

    /// <summary>Turns off the lingering <paramref name="parts"/> of the combo <c>{tag}</c> that show, because of
    /// <paramref name="reason"/> ("a reply" or "a try"). Returns how many showed.</summary>
    internal async Task<int> StopComboAsync(string tag, IReadOnlyList<CharacterActionSource> parts, string reason, CancellationToken token)
    {
        var off = new List<CharacterActionSource>();
        foreach (var source in parts)
            if (await StopActionAsync(source, "{/" + tag + "}", token).ConfigureAwait(false)) off.Add(source);
        if (off.Count == 0) return 0;
        var when = DateTime.Now.ToString("T", CultureInfo.CurrentCulture);
        Volatile.Write(ref lastAction, $"Combo {{/{tag}}} for {reason} at {when}: turned off {Names(off)}.");
        ActionPlayed?.Invoke();
        return off.Count;
    }

    // "\"blush\"", "\"blush\" and \"nod\"" or "\"blush\", \"hearts\" and \"nod\"".
    private static string Names(IReadOnlyList<CharacterActionSource> sources)
    {
        var names = sources.Select(s => $"\"{s.Name}\"").ToArray();
        return names.Length == 1 ? names[0] : string.Join(", ", names[..^1]) + " and " + names[^1];
    }
}
