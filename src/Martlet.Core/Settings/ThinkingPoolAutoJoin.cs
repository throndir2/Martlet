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
    /// <summary>It joined the pool by itself.</summary>
    Joined,
    /// <summary>A member on the host's Ollama now works on its Thinking pool role.</summary>
    MovedToRole,
    /// <summary>A member on its Thinking pool role follows the role's slot count.</summary>
    SlotsChanged
}

/// <summary>The pool after a host's check (<see cref="Pool"/>, the same object when nothing changed), what changed, the
/// computer's member and why.</summary>
public sealed record ThinkingPoolHostResult(ThinkingPoolSettings Pool, ThinkingPoolHostChange Change, DeepThinkingSettings? Member, string Why)
{
    public bool Changed => Change != ThinkingPoolHostChange.None;
}

/// <summary>Paired computers with a Thinking model join the Thinking pool by themselves, so the owner doesn't tick anything
/// when a computer comes online or gets a Thinking model. Each time a check sees a paired host answer:
/// <list type="bullet">
/// <item>A host that offers the Thinking pool role (<see cref="SelfHostSetup.DeepThinkingRouteId"/>) joins on that route, with
/// the role's slots.</item>
/// <item>A host that offers only its Ollama (<see cref="SelfHostSetup.OllamaRouteId"/>) joins on it, but only when it is not the
/// computer that does this PC's conversation Thinking (that model can't think something over while it answers you).</item>
/// <item>A member on its host's Ollama moves to the host's Thinking pool role once the host offers it; a member on the role
/// follows the role's slot count.</item>
/// </list>
/// A computer the owner took out (<see cref="ThinkingPoolSettings.LeftByOwner"/>), one Devices › Sharing work says the Thinking
/// pool never uses or keeps for other companion PCs, and any computer while the pool is full never joins. A host PC has no
/// Thinking pool. Endpoints (a cloud provider, Ollama on this PC) are added by the owner only.</summary>
public static class ThinkingPoolAutoJoin
{
    /// <summary>The route <paramref name="host"/> would work on in the pool: its Thinking pool role, else its Ollama when it isn't
    /// <paramref name="conversationThinkingHost"/>; null when it offers neither of those.</summary>
    public static ThinkingPoolOffer? Route(ThinkingPoolHost host, string? conversationThinkingHost)
    {
        ArgumentNullException.ThrowIfNull(host);
        return host.Offers.FirstOrDefault(o => o.RouteId == SelfHostSetup.DeepThinkingRouteId) ??
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
            HostRouteId = offer.RouteId == SelfHostSetup.DeepThinkingRouteId ? offer.RouteId : null, Slots = Slots(offer), ChosenAt = now
        };
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
        ThinkingPoolHostResult Changed(ThinkingPoolHostChange change, DeepThinkingSettings member)
        {
            try { member.Validate(); }
            catch (Exception error) when (error is ContractException or ArgumentException)
            {
                return Same($"{member.HostId} can't join the Thinking pool: {error.Message}");
            }
            return new(pool.Add(member), change, member, Describe(change, member));
        }
        if (hostPc) return Same("This PC is a host PC, so it has no Thinking pool.");
        var id = host.HostId;
        var role = host.Offers.FirstOrDefault(o => o.RouteId == SelfHostSetup.DeepThinkingRouteId);
        if (pool.Members.FirstOrDefault(m => m.Place == DeepThinkingPlace.Host && m.HostId == id) is { } member)
        {
            if (role is null) return Same($"{id} is in the pool already.", member);
            if (!member.OnHostRole) return Changed(ThinkingPoolHostChange.MovedToRole, Member(host, role, now ?? DateTimeOffset.Now));
            var slots = Slots(role);
            return member.Slots == slots ? Same($"{id} is in the pool already.", member)
                : Changed(ThinkingPoolHostChange.SlotsChanged, member with { Slots = slots });
        }
        if (pool.Left(id)) return Same($"You took {id} out of the Thinking pool. Tick it to add it again.");
        if (sharing?.Job(WorkSharingJobs.DeepThinking).Never.Contains(id, StringComparer.Ordinal) == true)
            return Same($"Devices › Sharing work says the Thinking pool never uses {id}.");
        if (sharing is not null && device is not null && !sharing.Allows(id, device))
            return Same($"{id} is kept for {string.Join(" and ", sharing.OnlyFor(id))} (Devices › Sharing work).");
        if (Route(host, conversationThinkingHost) is not { } route)
            return Same(role is null && host.HostId == conversationThinkingHost && host.Offers.Any(o => o.RouteId == SelfHostSetup.OllamaRouteId)
                ? $"{id}'s Ollama does this PC's conversation Thinking. Add the Thinking pool role there to join the pool beside it."
                : $"{id} has no Thinking model. Add the Thinking pool role there to join the pool.");
        if (pool.Members.Count >= DeepThinkingSettings.MaxPlaces)
            return Same($"The Thinking pool is full ({DeepThinkingSettings.MaxPlaces} members), so {id} doesn't join.");
        return Changed(ThinkingPoolHostChange.Joined, Member(host, route, now ?? DateTimeOffset.Now));
    }

    /// <summary>The owner's line for a change: "diva joined the Thinking pool by itself (its Thinking pool role, qwen3:8b, 2
    /// slots). Untick it in Companion › Thinking pool to keep it out."</summary>
    public static string Describe(ThinkingPoolHostChange change, DeepThinkingSettings member)
    {
        ArgumentNullException.ThrowIfNull(member);
        var slots = $"{member.ThinksAtOnce} slot{(member.ThinksAtOnce == 1 ? "" : "s")}";
        return change switch
        {
            ThinkingPoolHostChange.Joined => $"{member.HostId} joined the Thinking pool by itself " +
                $"({(member.OnHostRole ? "its Thinking pool role" : "its Ollama")}, {member.ModelId}, {slots}). " +
                "Untick it in Companion › Thinking pool to keep it out.",
            ThinkingPoolHostChange.MovedToRole => $"{member.HostId} now works for the Thinking pool on its Thinking pool role " +
                $"({member.ModelId}, {slots}) instead of its Ollama.",
            ThinkingPoolHostChange.SlotsChanged => $"{member.HostId}'s Thinking pool role now takes {slots} in the Thinking pool.",
            _ => $"{member.HostId} is in the Thinking pool."
        };
    }
}
