using Martlet.Core.Cluster;
using Martlet.Core.Settings;

namespace Martlet.Core.Tests;

public sealed class ThinkingPoolAutoJoinTests
{
    private static readonly ThinkingPoolOffer Role = new(SelfHostSetup.DeepThinkingRouteId, "qwen3:8b", 2);
    private static readonly ThinkingPoolOffer Ollama = new(SelfHostSetup.OllamaRouteId, "gemma4:e4b");
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 18, 0, 0, TimeSpan.Zero);

    private static ThinkingPoolHost Host(string id, params ThinkingPoolOffer[] offers) =>
        new(id, $"https://{id}.local:9443", "sha256:" + new string('0', 64), "desk-pc", Guid.NewGuid(), offers);

    [Fact]
    public void A_host_with_the_Thinking_pool_role_joins_with_its_slots()
    {
        var result = ThinkingPoolAutoJoin.For(new(), Host("diva", Ollama, Role), conversationThinkingHost: "diva", now: Now);

        Assert.Equal(ThinkingPoolHostChange.Joined, result.Change);
        var member = Assert.Single(result.Pool.Members);
        Assert.Equal("host:diva", member.Key);
        Assert.True(member.OnHostRole);
        Assert.Equal("qwen3:8b", member.ModelId);
        Assert.Equal(2, member.Slots);
        Assert.Equal(Now, member.ChosenAt);
        member.Validate();
        Assert.Equal("diva joined the Thinking pool by itself (its Thinking pool role, qwen3:8b, 2 slots). Untick it in Companion › " +
            "Thinking pool to keep it out.", result.Why);
    }

    [Fact]
    public void A_role_with_more_slots_than_the_pool_takes_is_capped()
    {
        var member = ThinkingPoolAutoJoin.For(new(), Host("diva", Role with { MaximumConcurrency = 32 }), null, now: Now).Member;
        Assert.Equal(DeepThinkingSettings.MaxPlaces, member?.Slots);
        Assert.Null(ThinkingPoolAutoJoin.For(new(), Host("diva", Role with { MaximumConcurrency = 1 }), null, now: Now).Member?.Slots);
    }

    [Fact]
    public void An_Ollama_only_host_joins_unless_it_does_this_PCs_Thinking()
    {
        var other = ThinkingPoolAutoJoin.For(new(), Host("quiet", Ollama), "ripley", now: Now);
        Assert.Equal(ThinkingPoolHostChange.Joined, other.Change);
        Assert.False(other.Member!.OnHostRole);
        Assert.Equal(SelfHostSetup.OllamaRouteId, other.Member.HostRoute);
        Assert.Contains("its Ollama, gemma4:e4b, 1 slot", other.Why, StringComparison.Ordinal);
        Assert.Equal(4, ThinkingPoolAutoJoin.For(new(), Host("quiet", Ollama with { MaximumConcurrency = 4 }), null, now: Now).Member?.Slots);

        var thinker = ThinkingPoolAutoJoin.For(new(), Host("ripley", Ollama), "ripley", now: Now);
        Assert.Equal(ThinkingPoolHostChange.None, thinker.Change);
        Assert.Empty(thinker.Pool.Members);
        Assert.Contains("conversation Thinking", thinker.Why, StringComparison.Ordinal);

        var nothing = ThinkingPoolAutoJoin.For(new(), Host("bare"), null, now: Now);
        Assert.Equal(ThinkingPoolHostChange.None, nothing.Change);
        Assert.Contains("no Thinking model", nothing.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_member_on_Ollama_moves_to_the_role_and_a_role_member_follows_its_slots()
    {
        var pool = ThinkingPoolAutoJoin.For(new(), Host("quiet", Ollama), null, now: Now).Pool.WithAnswers("host:quiet", true);

        var moved = ThinkingPoolAutoJoin.For(pool, Host("quiet", Ollama, Role), null, now: Now);
        Assert.Equal(ThinkingPoolHostChange.MovedToRole, moved.Change);
        var member = Assert.Single(moved.Pool.Members);
        Assert.True(member.OnHostRole);
        Assert.Equal(2, member.Slots);
        // The same key: Backup for slow replies stays as it was.
        Assert.True(moved.Pool.Answers("host:quiet"));

        var again = ThinkingPoolAutoJoin.For(moved.Pool, Host("quiet", Ollama, Role), null, now: Now);
        Assert.False(again.Changed);
        Assert.Same(moved.Pool, again.Pool);

        var more = ThinkingPoolAutoJoin.For(moved.Pool, Host("quiet", Role with { MaximumConcurrency = 3 }), null, now: Now);
        Assert.Equal(ThinkingPoolHostChange.SlotsChanged, more.Change);
        Assert.Equal(3, Assert.Single(more.Pool.Members).Slots);

        // A member stays when its computer no longer offers a Thinking model (offline or the role removed).
        Assert.False(ThinkingPoolAutoJoin.For(more.Pool, Host("quiet"), null, now: Now).Changed);
    }

    [Fact]
    public void Kept_out_never_used_kept_for_others_a_full_pool_and_a_host_PC_are_skipped()
    {
        var left = ThinkingPoolAutoJoin.For(new ThinkingPoolSettings().TakeOut("diva"), Host("diva", Role), null, now: Now);
        Assert.False(left.Changed);
        Assert.Contains("took diva out", left.Why, StringComparison.Ordinal);

        var sharing = new WorkSharingSettings().With(new WorkSharingJob { Job = WorkSharingJobs.DeepThinking, Never = ["diva"] });
        var never = ThinkingPoolAutoJoin.For(new(), Host("diva", Role), null, sharing, now: Now);
        Assert.False(never.Changed);
        Assert.Contains("never uses diva", never.Why, StringComparison.Ordinal);

        var kept = new WorkSharingSettings().With(new WorkSharingHost { HostId = "diva", OnlyFor = ["other-pc"] });
        Assert.False(ThinkingPoolAutoJoin.For(new(), Host("diva", Role), null, kept, "desk-pc", now: Now).Changed);
        Assert.True(ThinkingPoolAutoJoin.For(new(), Host("diva", Role), null, kept, "other-pc", now: Now).Changed);

        var full = Enumerable.Range(0, DeepThinkingSettings.MaxPlaces)
            .Aggregate(new ThinkingPoolSettings(), (pool, i) => ThinkingPoolAutoJoin.For(pool, Host("gpu-" + i, Role), null, now: Now).Pool);
        Assert.Equal(DeepThinkingSettings.MaxPlaces, full.Members.Count);
        var ninth = ThinkingPoolAutoJoin.For(full, Host("gpu-8", Role), null, now: Now);
        Assert.False(ninth.Changed);
        Assert.Contains("full", ninth.Why, StringComparison.Ordinal);

        Assert.False(ThinkingPoolAutoJoin.For(new(), Host("diva", Role), null, hostPc: true, now: Now).Changed);
    }

    [Fact]
    public void A_host_with_an_incomplete_pairing_does_not_join()
    {
        var plain = Host("diva", Role) with { Origin = "http://diva.local:9443" };
        var result = ThinkingPoolAutoJoin.For(new(), plain, null, now: Now);
        Assert.False(result.Changed);
        Assert.Empty(result.Pool.Members);
    }
}
