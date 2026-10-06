using System.Text;
using System.Text.Json;
using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

/// <summary>One moment through the real controller: a reply takes what else waits (what this PC played, finished background
/// work) and every request carries the same One moment instruction.</summary>
public sealed class MomentTurnDesktopTests
{
    private static readonly string Moment = LiveConversationConfiguration.Moment(null)!;

    [Fact]
    public async Task WhatThisPcPlayedBringsUpFinishedWorkOnlyWhenAskedAndThenGetsTools()
    {
        await using var fixture = await LiveFixture.Create(tools: true);
        var started = fixture.Controller.Jobs.Start(new BackgroundJobKind("think", 1, 10, TimeSpan.FromMinutes(5)), "Song for Biscuit",
            (_, _) => Task.FromResult(BackgroundJobOutcome.Done("Biscuit lyrics, verse one.")));
        Assert.NotNull(started.Job);
        await LiveConversationTests.Until(() => fixture.Controller.Jobs.HasNews);

        // On its own, what the PC played takes nothing more (and gets no tools).
        fixture.Answer("[pass]");
        var plain = fixture.Controller.Start("[PC audio] The boss is down!", voice: false, microphone: false, approved: true,
            spoken: true, pcAudio: true);
        await fixture.Finish(plain);
        Assert.Null(plain.Delivery);
        Assert.True(fixture.Controller.Jobs.HasNews);
        Assert.Empty(ToolNames(fixture.Llm.Body));
        Assert.Contains(Moment, Instructions(fixture.Llm.Body));
        Assert.Equal("1 line this PC played", plain.Inputs);

        // When Martlet may bring it up on its own, the same reply takes the finished work too, with the tools a report gets.
        fixture.Answer("Nice one! And that song for Biscuit is ready, want to hear it?");
        var moment = fixture.Controller.Start("[PC audio] Victory fanfare.", voice: false, microphone: false, approved: true,
            spoken: true, pcAudio: true, bringUp: true);
        await fixture.Finish(moment);
        Assert.Equal("runtime.Completed", moment.Status.Code);
        Assert.False(fixture.Controller.Jobs.HasNews);
        Assert.Equal(BackgroundDeliveryState.Delivered, started.Job!.Delivery);
        Assert.Contains("Biscuit lyrics, verse one.", Notes(fixture.Llm.Body));
        Assert.NotEmpty(ToolNames(fixture.Llm.Body));
        Assert.Contains(Moment, Instructions(fixture.Llm.Body));
        Assert.Equal("1 line this PC played and 1 finished job", moment.Inputs);
    }

    [Fact]
    public async Task WhatYouTypeTakesWhatThisPcPlayedAndEveryRequestStartsTheSameWay()
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Answer("Sure.");
        var typed = fixture.Start("What do you think?");
        await fixture.Finish(typed);
        var alone = Instructions(fixture.Llm.Body);
        Assert.Contains(Moment, alone);
        Assert.Equal("your words", typed.Inputs);

        fixture.Answer("Ha, that video is great.");
        var together = fixture.Controller.Start(LiveConversationWindow.PcMessage([("And now the cat jumps!", true), ("Look at this!", false)]),
            voice: false, microphone: false, approved: true, pcAudio: true, userWords: "Look at this!");
        await fixture.Finish(together);
        Assert.Equal("runtime.Completed", together.Status.Code);
        Assert.Equal("your words and 1 line this PC played", together.Inputs);
        var instructions = Instructions(fixture.Llm.Body);
        // The One moment instruction sits at the same place, so the start of the request stays the same.
        Assert.Equal(alone[..(alone.IndexOf(Moment, StringComparison.Ordinal) + Moment.Length)],
            instructions[..(instructions.IndexOf(Moment, StringComparison.Ordinal) + Moment.Length)]);
        Assert.Contains(LiveConversationConfiguration.PcAudioMarker + " And now the cat jumps!", Encoding.UTF8.GetString(fixture.Llm.Body));
    }

    [Fact]
    public async Task OnlyAMessageThatIsAllThePcsMayBringUpFinishedWork()
    {
        await using var fixture = await LiveFixture.Create();
        var error = Assert.Throws<LiveActionException>(() => fixture.Controller.Start("[PC audio] Hi.\nHello", voice: false,
            microphone: false, approved: true, spoken: true, pcAudio: true, userWords: "Hello", bringUp: true));
        Assert.Equal("conversation.invalid_input", error.Code);
        error = Assert.Throws<LiveActionException>(() => fixture.Controller.Start("Hello", voice: false, microphone: false,
            approved: true, bringUp: true));
        Assert.Equal("conversation.invalid_input", error.Code);
        fixture.NoEffects();
    }

    [Fact]
    public void TheTurnInputsLineSaysWhatTheLastReplyTook()
    {
        Assert.Equal("Last reply took your words and the picture.",
            LiveConversationWindow.TurnInputsLine(false, false, "your words and the picture", false));
        Assert.Equal("Last report took the picture and 1 finished job, counted as a look.",
            LiveConversationWindow.TurnInputsLine(false, true, "the picture and 1 finished job", true));
        Assert.Equal("Last look took the picture.", LiveConversationWindow.TurnInputsLine(true, false, "the picture", false));
        Assert.Contains("[pass]", Moment);
        Assert.Equal(Moment, PromptSettings.Fill(null, PromptCatalog.Moment, ("silent", LiveConversationConfiguration.SilentReply)));
    }

    private static string Instructions(byte[] body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("instructions", out var instructions) ? instructions.GetString() ?? "" : "";
    }

    private static string[] ToolNames(byte[] body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("tools", out var tools)
            ? tools.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!).ToArray() : [];
    }

    // The latest user message's text (with its notes).
    private static string Notes(byte[] body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("input").EnumerateArray()
            .Where(item => item.GetProperty("role").GetString() == "user")
            .Select(item => item.GetProperty("content") is { ValueKind: JsonValueKind.String } text ? text.GetString()!
                : item.GetProperty("content").EnumerateArray().Single(part => part.GetProperty("type").GetString() == "input_text")
                    .GetProperty("text").GetString()!)
            .Last();
    }
}
