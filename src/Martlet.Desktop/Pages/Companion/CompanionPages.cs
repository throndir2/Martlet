using Martlet.Core.Cluster;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>The Companion page's pages: the one place each choice that shapes Martlet is made, listed by group in a side list.
/// Home and Devices link here. A new page adds its value here (in list order) and one arm each in GroupOf, TabTitle, TabGlyph,
/// TabIntro and RenderTab.</summary>
internal enum CompanionTab
{
    Thinking, Listening, Voice, LipSync, DeepThinking, Singing, Pictures, Vision, Reading, Hearing, Motion, Profiles, Personality, Prompts, Skills, Lorebook,
    AppGuides, Memory, People, Character, SpeechBubbles, Emotes, Eyes, Touch, Replies, CheckIns, Tools, SmartHome, Discord, Messaging
}

/// <summary>The side list's groups, in order: how it works (the jobs Martlet needs, in priority order: Thinking, then listening,
/// voice and lip-sync), optional extras (parts Martlet works without, in the priority list's order: vision, reading, hearing, the
/// Thinking pool, smart home, singing and pictures; <see cref="OptionalExtras.Order"/>), who it is (personality and what it
/// knows), how it looks (the desktop character: its model, speech bubbles, emotes, eyes and touch) and what it does (how it
/// answers and acts). A group with no pages yet is not shown.</summary>
internal enum CompanionGroup { HowItWorks, Extras, WhoItIs, HowItLooks, WhatItDoes }

/// <summary>The Companion pages as a catalog, without WPF: which pages there are, their group, title, icon and intro, and the
/// setup job a page sets up. The side list and each page's title read it; MainWindow.Companion.cs draws the frame around the
/// pages. Martlet.Desktop.csproj imports this class everywhere (<c>&lt;Using Include="Martlet.Desktop.CompanionPages"
/// Static="true" /&gt;</c>), so MainWindow calls <c>TabTitle(...)</c> directly. A new page adds its value to
/// <see cref="CompanionTab"/> (in list order) and one arm each in GroupOf, TabTitle, TabGlyph, TabIntro and MainWindow's RenderTab.</summary>
internal static class CompanionPages
{
    /// <summary>The setup job a page sets up, or null for a page that isn't one of Setup's jobs.</summary>
    internal static SetupRole? JobRole(CompanionTab section) => section switch
    {
        CompanionTab.Thinking => SetupRole.Llm,
        CompanionTab.Voice => SetupRole.Tts,
        CompanionTab.Listening => SetupRole.Stt,
        _ => null
    };

    internal static CompanionTab TabFor(SetupRole role) => role switch
    {
        SetupRole.Tts => CompanionTab.Voice,
        SetupRole.Stt => CompanionTab.Listening,
        _ => CompanionTab.Thinking
    };

    internal static CompanionTab TabFor(string job) => job switch
    {
        ClusterJobs.Speaking => CompanionTab.Voice,
        ClusterJobs.Listening => CompanionTab.Listening,
        ClusterJobs.LipSync => CompanionTab.LipSync,
        _ => CompanionTab.Thinking
    };

    internal static CompanionGroup GroupOf(CompanionTab section) => section switch
    {
        CompanionTab.Thinking or CompanionTab.Voice or CompanionTab.Listening or CompanionTab.LipSync => CompanionGroup.HowItWorks,
        CompanionTab.DeepThinking or CompanionTab.Singing or CompanionTab.Pictures or CompanionTab.Vision or CompanionTab.Reading or
            CompanionTab.Hearing or CompanionTab.SmartHome or CompanionTab.Motion => CompanionGroup.Extras,
        CompanionTab.Profiles or CompanionTab.Personality or CompanionTab.Prompts or CompanionTab.Skills or CompanionTab.Lorebook or CompanionTab.AppGuides or CompanionTab.Memory or
            CompanionTab.People => CompanionGroup.WhoItIs,
        CompanionTab.Character or CompanionTab.SpeechBubbles or CompanionTab.Emotes or CompanionTab.Eyes or CompanionTab.Touch => CompanionGroup.HowItLooks,
        CompanionTab.Replies => CompanionGroup.WhatItDoes,
        CompanionTab.CheckIns => CompanionGroup.WhatItDoes,
        CompanionTab.Tools => CompanionGroup.WhatItDoes,
        CompanionTab.Discord => CompanionGroup.WhatItDoes,
        CompanionTab.Messaging => CompanionGroup.WhatItDoes,
        _ => CompanionGroup.WhatItDoes
    };

    /// <summary>The side list's pages in order: by group, then the jobs and the other groups in their list order, and the
    /// Optional extras in the priority list's order (<see cref="OptionalExtras.Order"/>).</summary>
    internal static IReadOnlyList<CompanionTab> SideListOrder() =>
    [
        .. Enum.GetValues<CompanionTab>().Select((tab, index) => (tab, index))
            .OrderBy(t => GroupOf(t.tab))
            .ThenBy(t => GroupOf(t.tab) == CompanionGroup.Extras ? OptionalExtras.Order(t.tab) : t.index)
            .Select(t => t.tab)
    ];

    internal static string GroupTitle(CompanionGroup group) => group switch
    {
        CompanionGroup.HowItWorks => "How it works",
        CompanionGroup.Extras => "Optional extras",
        CompanionGroup.WhoItIs => "Who it is",
        CompanionGroup.HowItLooks => "How it looks",
        _ => "What it does"
    };

    internal static string TabTitle(CompanionTab section) => section switch
    {
        CompanionTab.Thinking => "Thinking",
        CompanionTab.DeepThinking => "Background thinking",
        CompanionTab.Singing => "Singing",
        CompanionTab.Voice => "Voice",
        CompanionTab.Listening => "Listening",
        CompanionTab.Vision => "Vision",
        CompanionTab.Reading => "Reading",
        CompanionTab.Hearing => "Hearing",
        CompanionTab.LipSync => "Lip-sync",
        CompanionTab.Profiles => "Profiles",
        CompanionTab.Character => "Character",
        CompanionTab.SpeechBubbles => "Speech bubbles",
        CompanionTab.Emotes => "Emotes and motions",
        CompanionTab.Eyes => "Eyes",
        CompanionTab.Touch => "Touch",
        CompanionTab.Personality => "Personality",
        CompanionTab.Prompts => "Prompts",
        CompanionTab.Skills => "Skills",
        CompanionTab.Lorebook => "Lorebook",
        CompanionTab.AppGuides => "App guides",
        CompanionTab.Memory => "Memory",
        CompanionTab.People => "People",
        CompanionTab.Replies => "Replies",
        CompanionTab.CheckIns => "Check-ins",
        CompanionTab.Tools => "Tools",
        CompanionTab.Pictures => "Picture making",
        CompanionTab.Motion => "Motion",
        CompanionTab.SmartHome => "Smart home",
        CompanionTab.Discord => "Discord",
        CompanionTab.Messaging => "Messaging",
        _ => section.ToString()
    };

    /// <summary>The page's icon in the side list (Segoe Fluent Icons).</summary>
    internal static string TabGlyph(CompanionTab section) => section switch
    {
        CompanionTab.Thinking => "\uE82F",
        CompanionTab.DeepThinking => "\uE945",
        CompanionTab.Singing => "\uEC4F",
        CompanionTab.Voice => "\uE767",
        CompanionTab.Listening => "\uE720",
        CompanionTab.Vision => "\uE890",
        CompanionTab.Reading => "\uE7BC",
        CompanionTab.Hearing => "\uE7F6",
        CompanionTab.LipSync => "\uE8BD",
        CompanionTab.Profiles => "\uE748",
        CompanionTab.Character => "\uE77B",
        CompanionTab.SpeechBubbles => "\uE90A",
        CompanionTab.Emotes => "\uE899",
        CompanionTab.Eyes => "\uE7B3",
        CompanionTab.Touch => "\uE815",
        CompanionTab.Personality => "\uE76E",
        CompanionTab.Prompts => "\uE943",
        CompanionTab.Skills => "\uE82D",
        CompanionTab.Lorebook => "\uE736",
        CompanionTab.AppGuides => "\uE7FC",
        CompanionTab.Memory => "\uE8F1",
        CompanionTab.People => "\uE716",
        CompanionTab.Replies => "\uE8F2",
        CompanionTab.CheckIns => "\uE73E",
        CompanionTab.Tools => "\uE90F",
        CompanionTab.Pictures => "\uE8B9",
        CompanionTab.Motion => "\uE805",
        CompanionTab.SmartHome => "\uEC26",
        CompanionTab.Discord => "\uE902",
        CompanionTab.Messaging => "\uE724",
        _ => "\uE76E"
    };

    /// <summary>The line under the page title: what the page decides, in one or two sentences. Every Optional extras page starts
    /// with "Optional:" and ends with what Off means (<see cref="OptionalExtras.OffMeans"/>). The page shows only the first
    /// sentence; all of it is behind a "?" (Help-CompanionIntro).</summary>
    internal static string TabIntro(CompanionTab section) => section switch
    {
        CompanionTab.Thinking => "Martlet needs Thinking to answer you: choose where it thinks and which model it uses.",
        CompanionTab.DeepThinking => Extra(section, "Martlet thinks over hard questions and looks things up while you keep talking. Tick the computers and models it may use."),
        CompanionTab.Singing => Extra(section, "Martlet sings songs you ask for, in the voice it speaks with, on an NVIDIA graphics card."),
        CompanionTab.Voice => "Choose how Martlet speaks and where speech is generated.",
        CompanionTab.Listening => "Choose the microphone, push-to-talk mode and speech recognition.",
        CompanionTab.Vision => Extra(section, "Martlet looks at your screen or a camera once you press Start watching, with the model you choose."),
        CompanionTab.Reading => Extra(section, "While Martlet watches your screen, it reads the text on it: with Windows OCR on this PC or Martlet's Reading role."),
        CompanionTab.Hearing => Extra(section, "A model hears how you say things (your tone, laughter, sighs and the sounds around you), not only your words."),
        CompanionTab.LipSync => "Choose what moves the character's mouth.",
        CompanionTab.Profiles => "Switch who Martlet is in one step: each profile sets the character's look, voice and personality together.",
        CompanionTab.Character => "Choose Martlet's character, and its size and position on your desktop.",
        CompanionTab.SpeechBubbles => "Show Martlet's spoken words in a bubble beside the character, or as subtitles.",
        CompanionTab.Emotes => "Choose the emotes and motions that Martlet's replies can play on the character, and make combos of them.",
        CompanionTab.Eyes => "Choose where the character looks.",
        CompanionTab.Touch => "Choose what the character does when you click or stroke it: its touch zones, and how it feels about each touch.",
        CompanionTab.Personality => "Edit Martlet's personas.",
        CompanionTab.Prompts => "Every instruction Martlet sends to the Thinking model; your edited text is used instead of the built-in one.",
        CompanionTab.Skills => "Instructions Martlet uses only when it needs them, so every reply stays short and quick: pin the ones every reply must follow, and add your own.",
        CompanionTab.Lorebook => "Add lore entries Martlet can use when keywords come up.",
        CompanionTab.AppGuides => "Martlet reads up on the games and apps you use (their fan wikis and help pages) and answers your questions about them from what it read.",
        CompanionTab.Memory => "Facts Martlet remembers about you between conversations.",
        CompanionTab.People => "Teach Martlet whose voices it hears and the names they use.",
        CompanionTab.Replies => "Control reply length and creativity.",
        CompanionTab.CheckIns => "Every few minutes the Thinking pool checks what Martlet left on and said, and reminds it of what it forgot: small models forget a lot.",
        CompanionTab.Tools => "Let Martlet run terminal commands and use MCP tools while you talk, and choose when it must ask first.",
        CompanionTab.Pictures => Extra(section, "Martlet draws pictures when you ask: on your own graphics card or with a cloud provider."),
        CompanionTab.Motion => Extra(section, "Martlet makes new whole-body motions for VRM characters while you talk, on a computer with a 24 GB NVIDIA graphics card."),
        CompanionTab.SmartHome => Extra(section, "Find, set up or install Home Assistant, share it with your other computers, and let Martlet control your home when you ask."),
        CompanionTab.Discord => "Put Martlet on Discord: set up its bot, connect it, invite it to servers and choose where it chats.",
        CompanionTab.Messaging => "Talk to Martlet from Telegram or WhatsApp on your phone, with the same memory and personality, while Martlet runs on this PC.",
        _ => ""
    };

    /// <summary>An Optional extras page's intro: "Optional:" and what it does in one sentence (the page shows only that), then
    /// what Off means (behind the intro's "?").</summary>
    internal static string Extra(CompanionTab section, string what) => $"Optional: {what} Off: {OptionalExtras.OffMeans(section)}.";
}
