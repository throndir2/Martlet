using System.IO;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;

namespace Martlet.Desktop;

/// <summary>Where one of your hosts stands with one person: shared with them as a friend, asked for (they signed in but
/// aren't allowed yet), one of your own computers' sign-ins (allowed as a member) or not shared.</summary>
internal enum FriendHostState { NotShared, Asked, Shared, Member }

/// <summary>What one of your hosts says about a person: <paramref name="State"/>, their computers that signed in there
/// (<paramref name="Computers"/>), whether their provider is set up there so they can sign in (<paramref name="ProviderReady"/>)
/// and when they last asked (<paramref name="AskedAt"/>, while asking).</summary>
internal sealed record FriendHostView(string HostId, FriendHostState State, IReadOnlyList<HostSignInEnrolled> Computers, bool ProviderReady,
    DateTimeOffset? AskedAt = null);

/// <summary>One person you share hosts with, or who asked to use one: their identity at a provider (the host's allow list
/// names them by it) and each of your hosts that answered, in host order.</summary>
internal sealed record FriendView(string Provider, string Subject, string? Label, IReadOnlyList<FriendHostView> Hosts)
{
    public string Key => FriendsOverview.Key(Provider, Subject);
    public string Name => Label ?? Subject;
    public IEnumerable<string> SharedOn => Hosts.Where(h => h.State == FriendHostState.Shared).Select(h => h.HostId);
    public bool Asking => Hosts.Any(h => h.State == FriendHostState.Asked);
}

/// <summary>Devices › Friends: the people your hosts are shared with, built from each host's sign-in settings (read by a member
/// desktop over its signed connection; never a secret). A person is an identity allowed as a friend on any host, or one that
/// signed in to a host and isn't allowed yet, unless it is one of your own computers' sign-ins somewhere (allowed as a member):
/// those belong to that host's Sign-in from outside. Nothing here is cached for use; <see cref="SaveSummary"/> keeps a
/// non-secret summary (friends.json) only for MCP.</summary>
internal static class FriendsOverview
{
    internal const string SummaryFile = "friends.json";

    /// <summary>A stable automation-ID part for an identity: provider and subject with anything but letters, digits, dot and
    /// dash replaced.</summary>
    internal static string Key(string provider, string subject)
    {
        var text = provider + "-" + subject;
        return new string(text.Take(96).Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '_').ToArray());
    }

    internal static IReadOnlyList<FriendView> Build(IReadOnlyDictionary<string, HostSignInSettings> hosts)
    {
        var members = hosts.Values.SelectMany(s => s.Allowed.Where(a => !a.Friend)).Select(a => (a.Provider, a.Subject)).ToHashSet();
        var people = new Dictionary<(string Provider, string Subject), string?>();
        void Note(string provider, string subject, string? label) => people[(provider, subject)] = people.GetValueOrDefault((provider, subject)) ?? label;
        foreach (var settings in hosts.Values)
        {
            foreach (var friend in settings.Allowed.Where(a => a.Friend)) Note(friend.Provider, friend.Subject, friend.Label);
            foreach (var asked in settings.Refused.Where(r => !members.Contains((r.Provider, r.Subject)))) Note(asked.Provider, asked.Subject, asked.Label);
        }
        return people.Select(person =>
        {
            var (provider, subject) = person.Key;
            var views = hosts.OrderBy(h => h.Key, StringComparer.Ordinal).Select(h =>
            {
                var settings = h.Value;
                var allowed = settings.Allowed.FirstOrDefault(a => a.Provider == provider && a.Subject == subject);
                var asked = settings.Refused.Where(r => r.Provider == provider && r.Subject == subject)
                    .OrderByDescending(r => r.EnrolledAt).FirstOrDefault();
                var state = allowed is { Friend: true } ? FriendHostState.Shared
                    : allowed is not null ? FriendHostState.Member
                    : asked is not null ? FriendHostState.Asked
                    : FriendHostState.NotShared;
                var computers = settings.Enrolled.Where(e => e.Provider == provider && e.Subject == subject && e.Friend).ToArray();
                return new FriendHostView(h.Key, state, computers, settings.Providers.Any(p => p.Id == provider),
                    state == FriendHostState.Asked ? asked!.EnrolledAt : null);
            }).ToArray();
            return new FriendView(provider, subject, person.Value, views);
        })
        // Friends first (by name), then people asking, newest ask first.
        .OrderBy(f => f.SharedOn.Any() ? 0 : 1)
        .ThenByDescending(f => f.SharedOn.Any() ? DateTimeOffset.MinValue : f.Hosts.Max(h => h.AskedAt ?? DateTimeOffset.MinValue))
        .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    }

    /// <summary>The Friends card's status line.</summary>
    internal static string Status(IReadOnlyList<FriendView> friends, int hostsRead, int hostsTotal)
    {
        if (hostsTotal == 0) return "Pair a host first: you share your hosts' engines with friends from here.";
        var shared = friends.Count(f => f.SharedOn.Any());
        var asking = friends.Count(f => !f.SharedOn.Any() && f.Asking);
        var unread = hostsTotal - hostsRead;
        var limits = " Friends use only those hosts' engines, never join your Martlet network, and your own work always goes first.";
        var text = shared == 0 && asking == 0
            ? "You share no host with a friend yet. To share one: open its Sign-in from outside, set up a sign-in provider your friend has an " +
              "account with, make an invite and send it to them. Once they sign in, they show here and you share the host with them."
            : shared == 0
                ? $"{Count(asking, "person", "people")} asked to use one of your hosts: share it with them below." + limits
                : $"You share {Count(friends.Sum(f => f.SharedOn.Count()), "host")} with {Count(shared, "friend")}" +
                  (asking > 0 ? $"; {Count(asking, "person", "people")} asked to use a host." : ".") + limits;
        return unread > 0 ? text + $" {Count(unread, "host")} didn't answer." : text;

        static string Count(int count, string what, string? many = null) => count == 1 ? $"1 {what}" : $"{count} {many ?? what + "s"}";
    }

    /// <summary>Keeps what the Friends card last read in friends.json (host IDs, people's labels, providers, how many of their
    /// computers signed in; never a secret or address), so MCP network_status can show it without contacting a host.</summary>
    internal static void SaveSummary(string directory, IReadOnlyDictionary<string, HostSignInSettings> read,
        IReadOnlyDictionary<string, string> problems, DateTimeOffset at)
    {
        var document = new
        {
            version = 1,
            checkedAt = at,
            hosts = read.Keys.Concat(problems.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(id => new
            {
                hostId = id,
                read = read.ContainsKey(id),
                problem = problems.GetValueOrDefault(id),
                friends = read.TryGetValue(id, out var settings)
                    ? settings.Friends.Select(f => new
                    {
                        label = f.Label ?? f.Subject, provider = f.Provider,
                        computers = settings.Enrolled.Count(e => e.Provider == f.Provider && e.Subject == f.Subject && e.Friend)
                    }).ToArray()
                    : [],
                asking = read.TryGetValue(id, out var asked) ? asked.Refused.Count : 0
            }).ToArray()
        };
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, SummaryFile);
        var temporary = Path.Combine(directory, $"friends.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
