using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop.Tests;

// The image and audio models' pools on the desktop (docs/SENSE_MODELS.md). NOT AI: SenseAttemptRunner answers each attempt on a
// pool member in place of the member's model.
public sealed class SensePoolDesktopTests
{
    // Origins of their own, so no other test's job runs on these members in the shared queue at the same time.
    private static readonly DeepThinkingSettings Eyes = new()
    {
        Place = DeepThinkingPlace.Endpoint, Origin = "https://10.77.0.41:8444/v1", ModelId = "qwen2.5vl:7b"
    };

    private static readonly DeepThinkingSettings Spare = new()
    {
        Place = DeepThinkingPlace.Endpoint, Origin = "https://10.77.0.42:8444/v1", ModelId = "qwen2.5vl:7b"
    };

    private static readonly DeepThinkingSettings TextOnly = new()
    {
        Place = DeepThinkingPlace.Endpoint, Origin = "https://10.77.0.43:8444/v1", ModelId = "qwen3:8b"
    };

    private static SenseJob Look(bool onlyChosen = false) => new()
    {
        Purpose = "reply picture", Key = "picture", Instructions = "Describe what the user sees, in words for another model.",
        Text = "Window: a game.", Image = new([0xFF, 0xD8, 0xFF, .. new byte[32]], ImageMediaType.Jpeg, 4, 4), OnlyChosen = onlyChosen
    };

    private static async Task<LiveFixture> PoolFixture(List<(string Key, bool Fallback)> asked, Func<DeepThinkingSettings, SenseAttempt> answer)
    {
        var fixture = await LiveFixture.Create();
        // The Vision list: the image model, a text-only model (left out: it doesn't see) and a spare model that sees.
        fixture.Controller.SenseListsNow = new SenseSetup(new(), [Eyes, TextOnly, Spare], [], null, null, "lists");
        fixture.Controller.SenseAttemptRunner = (kind, member, fallback, job, _) =>
        {
            Assert.Equal(SenseKind.Image, kind);
            lock (asked) asked.Add((member.Key, fallback));
            return Task.FromResult(answer(member));
        };
        return fixture;
    }

    [Fact]
    public async Task A_busy_image_model_passes_the_picture_to_a_thinking_pool_member_that_sees()
    {
        var asked = new List<(string Key, bool Fallback)>();
        await using var fixture = await PoolFixture(asked, member => member.Key == Eyes.Key
            ? SenseAttempt.Busy("it is busy with another job") : SenseAttempt.Done(SenseAnswer.Done("Words from the spare model.")));
        Assert.Equal([Eyes.Key, Spare.Key], fixture.Controller.SenseMembers(SenseKind.Image, Eyes).Select(m => m.Key));

        var job = fixture.Controller.RunSenseAsync(SenseKind.Image, Look(), CancellationToken.None);
        await fixture.Advance(() => job.IsCompleted);
        var result = await job;

        Assert.Equal("Words from the spare model.", result.Text);
        Assert.Equal([(Eyes.Key, false), (Spare.Key, true)], asked);
        var status = fixture.Controller.SenseStatusJson();
        Assert.Contains("\"lane\": \"vision\"", status);
        Assert.Contains("\"elsewhere\": 1", status);
        Assert.Contains("\"member\": \"" + Spare.Key + "\"", status);
        Assert.Contains("\"busy\": 1", status);
        Assert.DoesNotContain(TextOnly.Key, status);
    }

    [Fact]
    public async Task A_free_image_model_gets_the_picture_alone_and_a_test_never_goes_to_another_model()
    {
        var asked = new List<(string Key, bool Fallback)>();
        var free = true;
        await using var fixture = await PoolFixture(asked, member => member.Key == Eyes.Key && !Volatile.Read(ref free)
            ? SenseAttempt.Unavailable("it is offline") : SenseAttempt.Done(SenseAnswer.Done("Words from " + member.Key)));

        var job = fixture.Controller.RunSenseAsync(SenseKind.Image, Look(), CancellationToken.None);
        await fixture.Advance(() => job.IsCompleted);
        Assert.Equal("Words from " + Eyes.Key, (await job).Text);
        Assert.Equal([(Eyes.Key, false)], asked);

        Volatile.Write(ref free, false);
        var test = fixture.Controller.RunSenseAsync(SenseKind.Image, Look(onlyChosen: true), CancellationToken.None);
        await fixture.Advance(() => test.IsCompleted);
        var tested = await test;
        Assert.Equal(SenseJobOutcome.Failed, tested.Outcome);
        Assert.Equal("it is offline", tested.Problem);
        Assert.Equal([(Eyes.Key, false), (Eyes.Key, false)], asked);
    }

    [Fact]
    public async Task The_first_model_in_the_list_that_sees_takes_the_pictures_and_an_empty_list_leaves_them_to_thinking()
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Controller.SenseListsNow = new SenseSetup(new(), [TextOnly, Eyes, Spare], [], null, null, "lists");
        var route = fixture.Controller.SenseRoute(SenseKind.Image);
        Assert.Equal((SensePath.Described, Eyes.Key), (route.Path, route.Model!.Key));
        Assert.Equal([Eyes.Key, Spare.Key], fixture.Controller.SenseMembers(SenseKind.Image, Eyes).Select(m => m.Key));

        fixture.Controller.SenseListsNow = new SenseSetup(new(), [], [], null, null, "lists");
        // The fixture's Thinking model (OpenAI) sees, so it takes the pictures itself.
        Assert.Equal(SensePath.Thinking, fixture.Controller.SenseRoute(SenseKind.Image).Path);
    }
}
