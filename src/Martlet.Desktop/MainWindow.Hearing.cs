using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>Companion › Hearing (an optional extra, docs/SENSE_MODELS.md): whether a model hears how you say things, not only
/// your words. In the standard order: Now (what hears your voice now, any problem, and the audio model's lines), then the main
/// choice (Off, Thinking's own model, the image model, Ollama on this PC, or a cloud provider or server;
/// <see cref="SenseChoiceCard"/>), then Hear how you say it (the consent check box, the voice path and Test hearing). Off is
/// Let ... hear my voice turned off (talk-preferences.json), so Thinking gets only the transcript. Automation IDs:
/// <c>HearingNow</c>, <c>TalkHearVoiceStatus</c>, the AudioModel lines, <c>Picker-Hearing-&lt;choice&gt;</c> and
/// <c>HearingToggle</c> (Turn hearing off on Off, Turn hearing on on the saved option while you turned hearing off).</summary>
public partial class MainWindow
{
    private void RenderHearingPage(Panel page)
    {
        var thinking = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        var abilities = SavedModelAbilities();
        var audio = SenseRouting.For(SenseKind.Audio, SenseModels.Load(store?.DataDirectory), thinking, abilities);
        var on = HearingOn(thinking, audio);
        var own = audio.Model;
        var hears = own is not null ? audio.Described : LiveConversationConfiguration.Hearing(thinking, abilities) == HearingSupport.Supported;

        var now = new TextBlock { Text = HearingNow(on, Talk.HearVoice, thinking, audio), FontSize = 15, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6) };
        AutomationProperties.SetAutomationId(now, "HearingNow");
        var advice = LiveConversationConfiguration.HearingAdvice(thinking, abilities, audio);
        var status = hears || !on ? Note(advice, new Thickness(0, 0, 0, 6)) : Warning(advice);
        AutomationProperties.SetAutomationId(status, "TalkHearVoiceStatus");
        page.Children.Add(Card([Heading("Now"), now, status, .. SenseNowLines(SenseKind.Audio)]));

        page.Children.Add(SenseChoiceCard(SenseKind.Audio, on,
            () => PageButton("Turn hearing off", () =>
            {
                SaveTalk(Talk with { HearVoice = false }, render: true);
                ActionText.Text = "Hearing is off. Thinking gets only the transcript of what you say.";
            }, primary: true, id: "HearingToggle"),
            () => Talk.HearVoice != false ? null : PageButton("Turn hearing on", () =>
            {
                SaveTalk(Talk with { HearVoice = null }, render: true);
                var nowOn = HearingOn(homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm), audio);
                ActionText.Text = nowOn ? "Hearing is on: your voice stays on this PC."
                    : "Your recording would leave this PC, so hearing waits for your tick under Hear how you say it.";
            }, primary: true, id: "HearingToggle"),
            Talk.HearVoice == false ? "Hearing is off. Turn it on to use this model."
                : "Off until you tick Let ... hear my voice below: your recording would leave this PC."));

        page.Children.Add(HearVoiceCard());
    }

    /// <summary>Whether a model hears your recording now: your own choice (Let ... hear my voice), or, never chosen, only while the
    /// recording stays on this PC where it goes.</summary>
    private bool HearingOn(SetupRoute? thinking, SenseRoute audio) =>
        (audio.Model is { } own ? Talk.HearVoiceFor(VoiceNotes.StaysOnThisPc(own)) : Talk.HearVoiceFor(thinking)).On;

    /// <summary>Companion › Hearing's Now line (HearingNow).</summary>
    internal static string HearingNow(bool on, bool? choice, SetupRoute? thinking, SenseRoute audio) =>
        !on ? $"Off. {OptionalExtras.OffMeans(CompanionTab.Hearing)}." +
              (choice is null ? " Your recording would leave this PC, so it waits for your tick below." : "")
        : audio.Model is { } own ? $"On. The audio model, {own.Describe()}, hears how you say things and describes it for Thinking."
        : thinking is null ? "On, but Thinking isn't set up yet."
        : $"On. Thinking ({thinking.ModelId}) hears your recording itself.";
}
