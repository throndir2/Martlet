using Martlet.Core.Cluster;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

/// <summary>A Thinking route a paired host offers, as its last check saw it: the route, its model and how many requests it
/// runs at once (a Thinking pool role's slots).</summary>
public sealed record ThinkingPoolOffer(string RouteId, string ModelId, int MaximumConcurrency = 1);

/// <summary>A paired host that answered a check: how this PC reaches it (its pairing: origin, pinned key, this PC's device ID
/// and pairing credential) and the routes it offers now.</summary>
public sealed record ThinkingPoolHost(string HostId, string Origin, string SpkiFingerprint, string DeviceId, Guid CredentialId,
    IReadOnlyList<ThinkingPoolOffer> Offers);

/// <summary>What a host's check changed in the Thinking pool.</summary>
public enum ThinkingPoolHostChange
{
    /// <summary>Nothing: see <see cref="ThinkingPoolHostResult.Why"/>.</summary>
    None,
    /// <summary>It joined the pool by itself (or one more of its graphics cards did).</summary>
    Joined,
    /// <summary>A member on the host's Ollama now works on its Thinking pool role.</summary>
    MovedToRole,
    /// <summary>A member on its Thinking pool role follows the role's slot count.</summary>
    SlotsChanged,
    /// <summary>A member on an extra graphics card left, because the host no longer runs a Thinking pool model there.</summary>
    CardRemoved
}

/// <summary>The pool after a host's check (<see cref="Pool"/>, the same object when nothing changed), what changed first, the
/// computer's (first changed) member and why. <see cref="Changes"/> lists every change: a host with a Thinking pool model on
/// each of its graphics cards has one member per card.</summary>
public sealed record ThinkingPoolHostResult(ThinkingPoolSettings Pool, ThinkingPoolHostChange Change, DeepThinkingSettings? Member, string Why)
{
    public bool Changed => Change != ThinkingPoolHostChange.None;

    /// <summary>Every change, in card order (the members that joined, moved, follow new slots or left).</summary>
    public IReadOnlyList<(ThinkingPoolHostChange Change, DeepThinkingSettings Member)> Changes { get; init; } = [];

    /// <summary>Whether only slot counts changed (nothing to tell the owner).</summary>
    public bool OnlySlots => Changes.Count > 0 && Changes.All(c => c.Change == ThinkingPoolHostChange.SlotsChanged);
}

/// <summary>Paired computers with a Thinking model join the Thinking pool by themselves, so the owner doesn't tick anything
/// when a computer comes online or gets a Thinking model. Each time a check sees a paired host answer:
/// <list type="bullet">
/// <item>A host that offers the Thinking pool role (<see cref="SelfHostSetup.DeepThinkingRouteId"/>) joins on that route, with
/// the role's slots. A host with a Thinking pool model on each of two or more graphics cards (deep-thinking-2... on
/// <see cref="SelfHostSetup.DeepThinkingRouteIdFor"/>) joins with one member per card (<c>host:diva</c>, <c>host:diva#gpu2</c>),
/// each with its own slots, so the board places work on each card on its own.</item>
/// <item>A host that offers only its Ollama (<see cref="SelfHostSetup.OllamaRouteId"/>) joins on it, but only when it is not the
/// computer that does this PC's conversation Thinking (that model can't think something over while it answers you).</item>
/// <item>A member on its host's Ollama moves to the host's Thinking pool role once the host offers it; a member on a role
/// follows the role's slot count; a member on an extra card leaves when the host no longer runs a model there.</item>
/// </list>
/// A computer the owner took out (<see cref="ThinkingPoolSettings.LeftByOwner"/>), a graphics card the owner turned off
/// (<see cref="ThinkingPoolSettings.OffMembers"/>; the computer's other cards still join), one Devices › Sharing work says the Thinking
/// pool never uses or keeps for other companion PCs, and any computer while the pool is full never joins. A host PC has no
/// Thinking pool. Endpoints (a cloud provider, Ollama on this PC) are added by the owner only.</summary>
public static class ThinkingPoolAutoJoin
{
    /// <summary>The Thinking pool roles <paramref name="host"/> offers, one per graphics card, card 1 first.</summary>
    public static IReadOnlyList<ThinkingPoolOffer> Roles(ThinkingPoolHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return [.. host.Offers.Where(o => SelfHostSetup.IsDeepThinkingRoute(o.RouteId))
            .OrderBy(o => SelfHostSetup.DeepThinkingCard(o.RouteId)).DistinctBy(o => o.RouteId)];
    }

    /// <summary>The route <paramref name="host"/> would work on in the pool first: its Thinking pool role (the lowest card), else
    /// its Ollama when it isn't <paramref name="conversationThinkingHost"/>; null when it offers neither of those.</summary>
    public static ThinkingPoolOffer? Route(ThinkingPoolHost host, string? conversationThinkingHost)
    {
        ArgumentNullException.ThrowIfNull(host);
        return Roles(host).FirstOrDefault() ??
            (host.HostId == conversationThinkingHost ? null : host.Offers.FirstOrDefault(o => o.RouteId == SelfHostSetup.OllamaRouteId));
    }

    /// <summary>A route's slots for a member: how many requests it runs at once when it says more than one (at most
    /// <see cref="DeepThinkingSettings.MaxPlaces"/>), else null (one).</summary>
    public static int? Slots(ThinkingPoolOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        return offer.MaximumConcurrency > 1 ? Math.Min(offer.MaximumConcurrency, DeepThinkingSettings.MaxPlaces) : null;
    }

    /// <summary>The member for <paramref name="host"/> on <paramref name="offer"/>.</summary>
    public static DeepThinkingSettings Member(ThinkingPoolHost host, ThinkingPoolOffer offer, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(offer);
        return new()
        {
            Place = DeepThinkingPlace.Host, ModelId = offer.ModelId, HostId = host.HostId, HostOrigin = host.Origin,
            HostSpkiFingerprint = host.SpkiFingerprint, HostDeviceId = host.DeviceId, HostCredentialId = host.CredentialId,
            HostRouteId = SelfHostSetup.IsDeepThinkingRoute(offer.RouteId) ? offer.RouteId : null, Slots = Slots(offer), ChosenAt = now
        };
    }

    /// <summary>Every member <paramref name="host"/> would have in the pool: one per Thinking pool role (graphics card), else
    /// one on its Ollama when it isn't <paramref name="conversationThinkingHost"/>; empty when it offers neither.</summary>
    public static IReadOnlyList<DeepThinkingSettings> Members(ThinkingPoolHost host, string? conversationThinkingHost, DateTimeOffset now)
    {
        var roles = Roles(host);
        return roles.Count > 0 ? [.. roles.Select(role => Member(host, role, now))]
            : Route(host, conversationThinkingHost) is { } route ? [Member(host, route, now)] : [];
    }

    /// <summary>What <paramref name="host"/>'s answer changes in <paramref name="pool"/>.</summary>
    /// <param name="conversationThinkingHost">The paired host that does this PC's conversation Thinking, or null.</param>
    /// <param name="sharing">Devices › Sharing work; null: no choice.</param>
    /// <param name="device">This PC's device ID, for computers kept for some companion PCs only.</param>
    /// <param name="hostPc">This PC is a host PC, which has no Thinking pool.</param>
    public static ThinkingPoolHostResult For(ThinkingPoolSettings pool, ThinkingPoolHost host, string? conversationThinkingHost,
        WorkSharingSettings? sharing = null, string? device = null, bool hostPc = false, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(host);
        ThinkingPoolHostResult Same(string why, DeepThinkingSettings? member = null) => new(pool, ThinkingPoolHostChange.None, member, why);
        if (hostPc) return Same("This PC is a host PC, so it has no Thinking pool.");
        var id = host.HostId;
        var at = now ?? DateTimeOffset.Now;
        var roles = Roles(host);
        var mine = pool.Members.Where(m => m.Place == DeepThinkingPlace.Host && m.HostId == id).ToArray();
        // The cards the owner turned off stay off: they never join or move by themselves.
        var off = pool.OffMembers.Where(m => m.Place == DeepThinkingPlace.Host && m.HostId == id).ToArray();
        bool Off(ThinkingPoolOffer offer) => pool.IsOff(DeepThinkingSettings.KeyOf(id, SelfHostSetup.DeepThinkingCard(offer.RouteId) ?? 1));
        if (mine.Length == 0)
        {
            if (pool.Left(id)) return Same($"You took {id} out of the Thinking pool. Tick it to add it again.");
            if (sharing?.Job(WorkSharingJobs.DeepThinking).Never.Contains(id, StringComparer.Ordinal) == true)
                return Same($"Devices › Sharing work says the Thinking pool never uses {id}.");
            if (sharing is not null && device is not null && !sharing.Allows(id, device))
                return Same($"{id} is kept for {string.Join(" and ", sharing.OnlyFor(id))} (Devices › Sharing work).");
            if (Route(host, conversationThinkingHost) is not { } first)
                return Same(roles.Count == 0 && host.HostId == conversationThinkingHost && host.Offers.Any(o => o.RouteId == SelfHostSetup.OllamaRouteId)
                    ? $"{id}'s Ollama does this PC's conversation Thinking. Add the Thinking pool role there to join the pool beside it."
                    : $"{id} has no Thinking model. Add the Thinking pool role there to join the pool.");
            if (off.Length > 0 && (roles.Count > 0 ? roles.All(Off) : Off(first)))
                return Same($"You turned {id} off in the Thinking pool. Tick On there to use it again.");
        }
        // A member stays when its computer offers no Thinking pool role now (offline, or the role removed): nothing to follow.
        if (mine.Length > 0 && roles.Count == 0) return Same($"{id} is in the pool already.", mine[0]);

        var next = pool;
        List<(ThinkingPoolHostChange Change, DeepThinkingSettings Member)> changes = [];
        List<string> notes = [];
        void Apply(ThinkingPoolHostChange change, DeepThinkingSettings member)
        {
            try { member.Validate(); }
            catch (Exception error) when (error is ContractException or ArgumentException)
            {
                notes.Add($"{id} can't join the Thinking pool: {error.Message}");
                return;
            }
            next = next.Add(member);
            changes.Add((change, member));
        }
        foreach (var offer in (roles.Count > 0 ? roles : [Route(host, conversationThinkingHost)!]).Where(o => !Off(o)))
        {
            var card = SelfHostSetup.DeepThinkingCard(offer.RouteId) ?? 1;
            var existing = next.Members.FirstOrDefault(m => m.Key == DeepThinkingSettings.KeyOf(id, card));
            if (existing is { OnHostRole: true } && existing.HostRouteId == offer.RouteId)
            {
                var slots = Slots(offer);
                if (existing.Slots != slots) Apply(ThinkingPoolHostChange.SlotsChanged, existing with { Slots = slots });
            }
            else if (existing is not null) Apply(ThinkingPoolHostChange.MovedToRole, Member(host, offer, at));
            else if (next.Members.Count >= DeepThinkingSettings.MaxPlaces)
                notes.Add($"The Thinking pool is full ({DeepThinkingSettings.MaxPlaces} members), so {id}" +
                    $"{(card > 1 ? $"'s graphics card {card}" : "")} doesn't join.");
            else Apply(ThinkingPoolHostChange.Joined, Member(host, offer, at));
        }
        // A Thinking pool model on an extra card that the host no longer runs leaves (card 1 stays, as above), also one turned off.
        foreach (var gone in mine.Concat(roles.Count > 0 ? off : []).Where(m => m.OnHostRole && m.Card > 1 && roles.All(r => r.RouteId != m.HostRouteId)))
        {
            next = next.Remove(gone.Key);
            changes.Add((ThinkingPoolHostChange.CardRemoved, gone));
        }
        if (changes.Count == 0) return Same(notes.FirstOrDefault() ?? $"{id} is in the pool already.", mine.FirstOrDefault());
        var joined = changes.Where(c => c.Change == ThinkingPoolHostChange.Joined).Select(c => c.Member).ToArray();
        var why = joined.Length > 1 && joined.Length == changes.Count && mine.Length == 0
            ? $"{id} joined the Thinking pool by itself with {joined.Length} graphics cards (" +
              string.Join("; ", joined.Select(m => $"card {m.Card}: {m.ModelId}, {SlotText(m)}")) +
              "). Untick it in Companion › Thinking pool to keep it out."
            : string.Join(" ", changes.Select(c => Describe(c.Change, c.Member)));
        return new(next, changes[0].Change, changes[0].Member, why) { Changes = changes };
    }

    private static string SlotText(DeepThinkingSettings member) => $"{member.ThinksAtOnce} slot{(member.ThinksAtOnce == 1 ? "" : "s")}";

    /// <summary>The owner's line for a change: "diva joined the Thinking pool by itself (its Thinking pool role, qwen3:8b, 2
    /// slots). Untick it in Companion › Thinking pool to keep it out."</summary>
    public static string Describe(ThinkingPoolHostChange change, DeepThinkingSettings member)
    {
        ArgumentNullException.ThrowIfNull(member);
        var slots = SlotText(member);
        var role = member.Card > 1 ? $"its Thinking pool model on graphics card {member.Card}" : "its Thinking pool role";
        return change switch
        {
            ThinkingPoolHostChange.Joined when member.Card > 1 => $"{member.HostId}'s graphics card {member.Card} joined the Thinking pool " +
                $"by itself ({member.ModelId}, {slots}), as a member of its own beside its other cards.",
            ThinkingPoolHostChange.Joined => $"{member.HostId} joined the Thinking pool by itself " +
                $"({(member.OnHostRole ? role : "its Ollama")}, {member.ModelId}, {slots}). " +
                "Untick it in Companion › Thinking pool to keep it out.",
            ThinkingPoolHostChange.MovedToRole => $"{member.HostId} now works for the Thinking pool on {role} " +
                $"({member.ModelId}, {slots}) instead of its Ollama.",
            ThinkingPoolHostChange.SlotsChanged => $"{member.HostId}'s {(member.Card > 1 ? $"Thinking pool model on graphics card {member.Card}" : "Thinking pool role")} now takes {slots} in the Thinking pool.",
            ThinkingPoolHostChange.CardRemoved => $"{member.HostId}'s graphics card {member.Card} left the Thinking pool: it no longer runs a Thinking pool model there.",
            _ => $"{member.HostId} is in the Thinking pool."
        };
    }
}
