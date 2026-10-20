using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>Companion › Hearing's decisions and words, without WPF. The page makes one each time it draws: it reads how you talk,
/// the Thinking route and where recordings go once, says what the Now card shows and saves Turn hearing off or on
/// (talk-preferences.json). <see cref="HearingPage"/> draws it, and <see cref="IHearingPageServices"/> is all it reads and changes
/// in the app.</summary>
internal sealed class HearingPageModel
{
    private readonly IHearingPageServices services;

    internal HearingPageModel(IHearingPageServices services)
    {
        this.services = services;
        Prefs = services.Talk;
        Thinking = services.Settings.ThinkingRoute();
        var abilities = services.SavedModelAbilities();
        Audio = services.AudioRoute(Thinking, abilities);
        On = IsOn(Prefs, Thinking, Audio);
        Hears = Audio.Model is not null ? Audio.Described : LiveConversationConfiguration.Hearing(Thinking, abilities) == HearingSupport.Supported;
        NowText = Now(On, Prefs.HearVoice, Thinking, Audio);
        Advice = LiveConversationConfiguration.HearingAdvice(Thinking, abilities, Audio);
    }

    /// <summary>How you talk with Martlet, as saved when the page drew.</summary>
    internal TalkPreferences Prefs { get; }
    /// <summary>The conversation's own Thinking route, or null before Thinking is set up.</summary>
    internal SetupRoute? Thinking { get; }
    /// <summary>Where a recording with a reply goes.</summary>
    internal SenseRoute Audio { get; }
    /// <summary>Whether a model hears your recording now.</summary>
    internal bool On { get; }
    /// <summary>Whether the model that would get your recording can hear it.</summary>
    internal bool Hears { get; }
    /// <summary>The Now line (HearingNow).</summary>
    internal string NowText { get; }
    /// <summary>The line under it (TalkHearVoiceStatus): a note, or a warning while hearing is on but the model can't hear.</summary>
    internal string Advice { get; }
    /// <summary>Whether <see cref="Advice"/> is a warning.</summary>
    internal bool AdviceWarns => !Hears && On;

    /// <summary>The words of the HearingToggle button: Turn hearing off while a model hears you, Turn hearing on after you turned
    /// it off, or null (no button) when it is off and you never chose.</summary>
    internal string? ToggleText => On ? "Turn hearing off" : Prefs.HearVoice != false ? null : "Turn hearing on";

    /// <summary>Turn hearing off or on, as <see cref="ToggleText"/> says.</summary>
    internal void Toggle()
    {
        if (On)
        {
            services.SaveTalk(services.Talk with { HearVoice = false }, render: true);
            services.ShowStatus("Hearing is off, so Thinking gets only the transcript of what you say.");
            return;
        }
        services.SaveTalk(services.Talk with { HearVoice = null }, render: true);
        var nowOn = IsOn(services.Talk, services.Settings.ThinkingRoute(), Audio);
        services.ShowStatus(nowOn ? "Hearing is on: your voice stays on this PC."
            : "Your recording would leave this PC, so hearing waits for your tick under Hear how you say it.");
    }

    /// <summary>Whether a model hears your recording now: your own choice (Let ... hear my voice), or, never chosen, only while the
    /// recording stays on this PC where it goes.</summary>
    internal static bool IsOn(TalkPreferences talk, SetupRoute? thinking, SenseRoute audio) =>
        (audio.Model is { } own ? talk.HearVoiceFor(VoiceNotes.StaysOnThisPc(own)) : talk.HearVoiceFor(thinking)).On;

    /// <summary>Companion › Hearing's Now line (HearingNow).</summary>
    internal static string Now(bool on, bool? choice, SetupRoute? thinking, SenseRoute audio) =>
        !on ? $"Off: {OptionalExtras.OffMeans(CompanionTab.Hearing)}" + (choice is not null ? "."
            : thinking is null && audio.Model is null ? "; set up Thinking first."
            : "; your recording would leave this PC, so it waits for your tick below.")
        : audio.Model is { } own ? $"On: the audio model, {own.Describe()}, hears how you say things and describes it for Thinking."
        : thinking is null ? "On, but Thinking isn't set up yet."
        : $"On: Thinking ({thinking.ModelId}) hears your recording itself.";
}
