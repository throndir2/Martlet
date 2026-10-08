namespace Martlet.Avatar.Hosting;

/// <summary>A lingering emote the character shows: what it is, on which model (its path), since when and why it went on
/// (<paramref name="Why"/>: a reply's tag such as "{blush}", "a try", a touch...).</summary>
public sealed record HeldEmote(CharacterActionSource Source, string? ModelPath, DateTimeOffset Since, string? Why = null)
{
    /// <summary>Whether a reply's tag turned it on (its own tag or a combo's), not the owner's Try or a touch.</summary>
    public bool ByReply => Why is { Length: > 1 } why && why[0] == '{';
}

/// <summary>The lingering emotes the character shows now (turned on by <c>{tag}</c>, until <c>{/tag}</c>, Clear emotes or
/// another model), oldest first. At most <see cref="Maximum"/>: one more lets the oldest go. Thread-safe.</summary>
public sealed class HeldEmotes
{
    public const int Maximum = 8;
    private readonly object gate = new();
    private IReadOnlyList<HeldEmote> held = [];

    /// <summary>Raised (on the caller's thread) after the list changes.</summary>
    public event Action? Changed;

    public IReadOnlyList<HeldEmote> Current => Volatile.Read(ref held);

    public bool Holds(string id) => Current.Any(h => h.Source.Id == id);

    /// <summary>Adds <paramref name="source"/>, turned on because of <paramref name="why"/> (no change when it already shows).
    /// Returns the emote that had to go to make room, if any.</summary>
    public HeldEmote? Add(CharacterActionSource source, string? modelPath, DateTimeOffset now, string? why = null)
    {
        HeldEmote? dropped = null;
        lock (gate)
        {
            if (held.Any(h => h.Source.Id == source.Id)) return null;
            var next = held.ToList();
            if (next.Count >= Maximum)
            {
                dropped = next[0];
                next.RemoveAt(0);
            }
            next.Add(new(source, modelPath, now, why));
            Volatile.Write(ref held, next.ToArray());
        }
        Changed?.Invoke();
        return dropped;
    }

    /// <summary>Removes the emote <paramref name="id"/>; returns whether it showed.</summary>
    public bool Remove(string id)
    {
        lock (gate)
        {
            if (!held.Any(h => h.Source.Id == id)) return false;
            Volatile.Write(ref held, held.Where(h => h.Source.Id != id).ToArray());
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Forgets every emote; returns those that showed.</summary>
    public IReadOnlyList<HeldEmote> Clear()
    {
        IReadOnlyList<HeldEmote> was;
        lock (gate)
        {
            was = held;
            Volatile.Write(ref held, Array.Empty<HeldEmote>());
        }
        if (was.Count > 0) Changed?.Invoke();
        return was;
    }
}
