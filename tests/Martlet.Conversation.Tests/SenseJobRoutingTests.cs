using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Conversation.Tests;

// Which model takes each picture and recording job (SenseJobRouting, docs/SENSE_MODELS.md, Who takes each picture and recording),
// for every setup: by fixture model names, nothing is sent.
public sealed class SenseJobRoutingTests
{
    private const string Ollama = GenerationSupport.LocalOllamaChatBaseUrl;

    private static SetupRoute Chat(string model) => new()
    {
        RouteType = SetupRouteType.ChatCompletions, Role = SetupRole.Llm, ProviderAlias = ChatCompletionsSetup.Alias, Origin = Ollama,
        ModelId = model, ConfigurationRevision = Guid.NewGuid(), Enabled = true
    };

    private static SenseModel Own(string model) =>
        new() { Source = SenseSource.Own, Own = new() { Place = DeepThinkingPlace.Endpoint, Origin = "https://10.77.0.20:8444/v1", ModelId = model } };

    // Thinking models: Gemma 4 E2B sees and hears, Qwen3 8B is text-only, my-own-model can't be told about.
    public enum Thinker { None, Omni, TextOnly, Unknown }

    private static SetupRoute? Thinking(Thinker thinker) => thinker switch
    {
        Thinker.Omni => Chat("gemma4:e2b"),
        Thinker.TextOnly => Chat("qwen3:8b"),
        Thinker.Unknown => Chat("my-own-model"),
        _ => null
    };

    public static TheoryData<SenseJobKind, Thinker, bool, bool, bool> Setups()
    {
        var data = new TheoryData<SenseJobKind, Thinker, bool, bool, bool>();
        foreach (var job in Enum.GetValues<SenseJobKind>())
            foreach (var thinker in Enum.GetValues<Thinker>())
                foreach (var own in new[] { false, true })
                    foreach (var pool in new[] { false, true })
                        foreach (var extra in new[] { false, true })
                            data.Add(job, thinker, own, pool, extra);
        return data;
    }

    // The documented orders, written out on their own (not read from the table): the test fails when the code changes one.
    private static SenseTaker Expected(SenseJobKind job, Thinker thinker, bool own, bool pool, bool extra)
    {
        var sees = thinker is Thinker.Omni or Thinker.Unknown;
        var hears = thinker == Thinker.Omni;
        return job switch
        {
            SenseJobKind.ReplyPicture => own ? SenseTaker.SenseModel : sees ? SenseTaker.ThinkingModel : SenseTaker.Nobody,
            SenseJobKind.ScreenSummary => own ? SenseTaker.SenseModel : pool ? SenseTaker.PoolMember : SenseTaker.Nobody,
            SenseJobKind.HelperPicture => pool ? SenseTaker.PoolMember : own ? SenseTaker.SenseModel : sees ? SenseTaker.ThinkingModel : SenseTaker.Nobody,
            SenseJobKind.CheckInScreenshot or SenseJobKind.CheckInRecording => pool ? SenseTaker.PoolMember : SenseTaker.Nobody,
            // extra: Hear how you say it is off.
            SenseJobKind.ReplyVoice => extra ? SenseTaker.Nobody : own ? SenseTaker.SenseModel : hears ? SenseTaker.ThinkingModel : SenseTaker.Nobody,
            // extra: the sound tagger is in Martlet's folder.
            SenseJobKind.PcSound => own ? SenseTaker.SenseModel : pool ? SenseTaker.PoolMember : extra ? SenseTaker.ThisPc : SenseTaker.Nobody,
            _ => throw new ArgumentOutOfRangeException(nameof(job))
        };
    }

    private static SenseDecision Decide(SenseJobKind job, Thinker thinker, bool own, bool pool, bool extra)
    {
        var row = SenseJobRouting.Row(job);
        var image = row.Kind == SenseKind.Image;
        var senses = own ? image ? new SenseModels { Image = Own("qwen2.5vl:7b") } : new SenseModels { Audio = Own("gemma3n:e4b") } : new SenseModels();
        var inputs = SenseJobInputs.For(row.Kind, senses, Thinking(thinker), null) with
        {
            PoolName = pool ? image ? "diva (qwen2.5vl:7b)" : "diva (gemma4:e4b)" : null, PoolKey = pool ? "host:diva" : null,
            ThisPc = extra, VoiceAllowed = !extra
        };
        return SenseJobRouting.Decide(job, inputs);
    }

    [Theory]
    [MemberData(nameof(Setups))]
    public void Each_job_goes_to_the_first_taker_in_its_order(SenseJobKind job, Thinker thinker, bool own, bool pool, bool extra)
    {
        var decision = Decide(job, thinker, own, pool, extra);
        Assert.Equal(Expected(job, thinker, own, pool, extra), decision.Taker);
        Assert.Equal(SenseJobRouting.Row(job).Order, decision.Steps.Select(s => s.Taker));
        Assert.All(decision.Steps, step => Assert.False(string.IsNullOrWhiteSpace(step.Why)));
        if (decision.Taker is SenseTaker.SenseModel or SenseTaker.PoolMember) Assert.Contains(decision.Name!, decision.Why);
    }

    [Fact]
    public void The_table_has_one_row_for_each_job_and_ends_each_order_with_no_one()
    {
        Assert.Equal(Enum.GetValues<SenseJobKind>(), SenseJobRouting.Rows.Select(r => r.Job));
        foreach (var row in SenseJobRouting.Rows)
        {
            Assert.Equal(SenseTaker.Nobody, row.Order[^1]);
            Assert.Single(row.Order, taker => taker == SenseTaker.Nobody);
            Assert.Equal(row.Order.Distinct().Count(), row.Order.Count);
            // A job that goes to a pool member names its pool kind, and fits what that kind's row says its jobs may ask.
            Assert.Equal(row.PoolKind is not null, row.Order.Contains(SenseTaker.PoolMember));
            if (row.PoolKind is { } kind) Assert.Equal(row.Needs, ThinkingJobKinds.Row(kind).Needs & row.Needs);
        }
    }

    [Fact]
    public void Screen_summaries_never_go_to_thinking_and_helper_jobs_try_the_pool_first()
    {
        // A screen summary would evict the conversation from Thinking's prompt cache.
        Assert.DoesNotContain(SenseTaker.ThinkingModel, SenseJobRouting.Row(SenseJobKind.ScreenSummary).Order);
        // HelperJobs.RunAsync tries the pool first; its fallback reads the rest of this order.
        Assert.Equal(SenseTaker.PoolMember, SenseJobRouting.Row(SenseJobKind.HelperPicture).Order[0]);
        Assert.Equal(new[] { SenseTaker.SenseModel, SenseTaker.PoolMember }, SenseJobRouting.Row(SenseJobKind.ScreenSummary).Order.Take(2));
        Assert.Equal(new[] { SenseTaker.SenseModel, SenseTaker.PoolMember }, SenseJobRouting.Row(SenseJobKind.PcSound).Order.Take(2));
    }

    [Fact]
    public void The_step_after_the_one_that_takes_it_is_what_runs_when_it_cant()
    {
        var summary = Decide(SenseJobKind.ScreenSummary, Thinker.TextOnly, own: true, pool: true, extra: false);
        Assert.Equal(SenseTaker.SenseModel, summary.Taker);
        Assert.Equal("10.77.0.20 (qwen2.5vl:7b)", summary.Name);
        Assert.Equal(("diva (qwen2.5vl:7b)", SenseTaker.PoolMember), (summary.Then?.Name, summary.Then?.Taker));
        var helper = Decide(SenseJobKind.HelperPicture, Thinker.Omni, own: true, pool: true, extra: false);
        Assert.Equal(SenseTaker.SenseModel, helper.After(SenseTaker.PoolMember)?.Taker);
        Assert.Equal(SenseTaker.ThinkingModel, Decide(SenseJobKind.HelperPicture, Thinker.Omni, own: false, pool: true, extra: false).After(SenseTaker.PoolMember)?.Taker);
        Assert.Null(Decide(SenseJobKind.CheckInScreenshot, Thinker.Omni, own: true, pool: true, extra: false).Then);
    }

    [Fact]
    public void The_reply_jobs_say_what_sense_routing_says()
    {
        foreach (var thinker in Enum.GetValues<Thinker>())
            foreach (var own in new[] { false, true })
            {
                var senses = own ? new SenseModels { Image = Own("qwen2.5vl:7b") } : new SenseModels();
                var route = SenseRouting.For(SenseKind.Image, senses, Thinking(thinker), null);
                var decision = Decide(SenseJobKind.ReplyPicture, thinker, own, pool: false, extra: false);
                Assert.Equal(route.Why, decision.Why);
            }
    }

    [Fact]
    public void An_empty_list_says_who_takes_each_job_instead()
    {
        var decisions = SenseJobRouting.Of(SenseKind.Image).Select(row => Decide(row.Job, Thinker.Omni, own: false, pool: true, extra: false)).ToArray();
        Assert.Equal("Thinking's own model (gemma4:e2b) takes the picture with a reply; diva (qwen2.5vl:7b) in the Thinking pool takes " +
            "screen summaries, touch zones, the character's eyes and check-ins with a screenshot", SenseJobRouting.Instead(decisions));

        var alone = SenseJobRouting.Of(SenseKind.Image).Select(row => Decide(row.Job, Thinker.TextOnly, own: false, pool: false, extra: false)).ToArray();
        Assert.Equal("the picture with a reply: the reply goes without a picture; screen summaries: no model makes screen summaries now; " +
            "touch zones and the character's eyes: no model can see the pictures; check-ins with a screenshot: they wait for a Thinking " +
            "pool member that sees", SenseJobRouting.Instead(alone));

        var sounds = SenseJobRouting.Of(SenseKind.Audio).Select(row => Decide(row.Job, Thinker.TextOnly, own: false, pool: false, extra: true)).ToArray();
        Assert.Contains("the sound tagger on this PC takes descriptions of PC sounds", SenseJobRouting.Instead(sounds));
    }

    [Fact]
    public void The_inputs_must_be_for_the_jobs_kind()
    {
        var audio = SenseJobInputs.For(SenseKind.Audio, new(), Chat("gemma4:e2b"), null);
        Assert.Throws<ArgumentException>(() => SenseJobRouting.Decide(SenseJobKind.ScreenSummary, audio));
    }
}
