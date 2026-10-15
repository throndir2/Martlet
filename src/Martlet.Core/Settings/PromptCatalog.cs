using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

/// <summary>One internal prompt Martlet sends to the Thinking model, editable on Companion › Prompts. Placeholders such as
/// <c>{name}</c> are filled in by Martlet when the prompt is sent.</summary>
public sealed record PromptDefinition(string Id, string Group, string Title, string Help, string Default, IReadOnlyList<string> Placeholders);

/// <summary>Every internal prompt Martlet sends to the Thinking model, with its built-in text. The user's edits are saved in
/// <see cref="PromptSettings"/>; whatever is not edited uses the text here. This file has the prompt IDs, the groups and the
/// look-ups; PromptCatalog.Definitions.cs has each prompt's labels (<see cref="All"/>) and PromptCatalog.Texts.cs its
/// built-in text.</summary>
public static partial class PromptCatalog
{
    public const string Persona = "persona";
    public const string ReplyLength = "reply_length";
    public const string ShortFirstSentence = "short_first_sentence";
    public const string Listening = "listening";
    public const string PcAudio = "pc_audio";
    public const string DiscordCall = "discord_call";
    public const string Moment = "moment";
    public const string SaidLately = "said_lately";
    public const string MomentAttention = "moment_attention";
    public const string Tools = "tools";
    public const string VoiceTags = "voice_tags";
    public const string CharacterActions = "character_actions";
    public const string CharacterShowing = "character_showing";
    public const string CharacterGaze = "character_gaze";
    public const string CharacterLooking = "character_looking";
    public const string Voices = "voices";
    public const string HeardVoice = "heard_voice";
    public const string HeardVoiceOnly = "heard_voice_only";
    public const string HeardVoiceDescribed = "heard_voice_described";
    public const string HeardVoiceNote = "heard_voice_note";
    public const string VoiceDescription = "voice_description";
    public const string Lorebook = "lorebook";
    public const string MemoryRecall = "memory_recall";
    public const string PastConversations = "past_conversations";
    public const string MemoryPeople = "memory_people";
    public const string MemoryBrief = "memory_brief";
    public const string MemoryBriefWriting = "memory_brief_writing";
    public const string Notes = "notes";
    public const string GlanceScreen = "glance_screen";
    public const string GlanceCamera = "glance_camera";
    public const string GlanceAttention = "glance_attention";
    public const string GlanceLook = "glance_look";
    public const string CommentaryScreen = "commentary_screen";
    public const string CommentaryCamera = "commentary_camera";
    public const string SeenWithMessage = "seen_with_message";
    public const string SeenApp = "seen_app";
    public const string SeenTag = "seen_tag";
    public const string SeenDescribed = "seen_described";
    public const string SeenDescribedNote = "seen_described_note";
    public const string DescribePicture = "describe_picture";
    public const string ReadOnScreen = "read_on_screen";
    public const string ScreenDigest = "screen_digest";
    public const string PersonalPrivate = "personal_private";
    public const string PersonalShared = "personal_shared";
    public const string ChattinessQuiet = "chattiness_quiet";
    public const string ChattinessNormal = "chattiness_normal";
    public const string ChattinessChatty = "chattiness_chatty";
    public const string ChattinessDecides = "chattiness_decides";
    public const string ChattinessNow = "chattiness_now";
    public const string MemoryCapture = "memory_capture";
    public const string MemoryUpkeep = "memory_upkeep";
    public const string VoiceNaming = "voice_naming";
    public const string CharacterActionNaming = "character_action_naming";
    public const string CharacterWriting = "character_writing";
    public const string HomeWrap = "home_wrap";
    public const string HomeDone = "home_done";
    public const string HomeAnswer = "home_answer";
    public const string HomeFailed = "home_failed";
    public const string HomeTools = "home_tools";
    public const string HomeLocksConfirm = "home_locks_confirm";
    public const string HomeLocksOff = "home_locks_off";
    public const string HomeNotRecognized = "home_not_recognized";
    public const string HomeBlocked = "home_blocked";
    public const string HomeDeclined = "home_declined";
    public const string HomeUnreachable = "home_unreachable";
    public const string ThinkLonger = "think_longer";
    public const string HandedOffTools = "handed_off_tools";
    public const string BackgroundThink = "background_think";
    public const string BackgroundDone = "background_done";
    public const string BackgroundDoneNotes = "background_done_notes";
    public const string ReminderDue = "reminder_due";
    public const string ReminderDueNotes = "reminder_due_notes";
    public const string Greeting = "greeting";
    public const string GreetingNotes = "greeting_notes";
    public const string Touched = "touched";
    public const string TouchedNotes = "touched_notes";
    public const string TouchedCutIn = "touched_cut_in";
    public const string CutOff = "cut_off";
    public const string AdultContent = "adult_content";
    public const string Singing = "singing";
    public const string SingingTags = "singing_tags";
    public const string WhileSinging = "while_singing";
    public const string SongLyrics = "song_lyrics";
    public const string WebResearch = "web_research";
    public const string ResearchStep = "research_step";
    public const string ResearchFound = "research_found";
    public const string ResearchFoundNotes = "research_found_notes";
    public const string ResearchSourceCheck = "research_source_check";
    public const string AppGuides = "app_guides";
    public const string AppGuideNotes = "app_guide_notes";
    public const string AppGuideBrief = "app_guide_brief";
    public const string AppGuideOffer = "app_guide_offer";
    public const string AppGuideOfferNotes = "app_guide_offer_notes";
    public const string CheckIn = "check_in";
    public const string CheckInEmotes = "check_in_emotes";
    public const string CheckInGaze = "check_in_gaze";
    public const string CheckInPromises = "check_in_promises";
    public const string CheckInCharacter = "check_in_character";
    public const string CheckInRepeats = "check_in_repeats";
    public const string CheckInTouches = "check_in_touches";
    public const string CheckInWelcome = "check_in_welcome";
    public const string CheckInUnanswered = "check_in_unanswered";
    public const string CheckInAside = "check_in_aside";
    public const string CheckInAsideNote = "check_in_aside_note";
    public const string CheckInOthers = "check_in_others";
    public const string CheckInReactions = "check_in_reactions";
    public const string CheckInActions = "check_in_actions";
    public const string CheckInAhead = "check_in_ahead";
    public const string CheckInCustom = "check_in_custom";
    public const string CheckInNote = "check_in_note";
    public const string CheckInContext = "check_in_context";
    public const string CheckInPlan = "check_in_plan";
    public const string CheckInDue = "check_in_due";
    public const string CheckInDueNotes = "check_in_due_notes";
    public const string CheckInAdultOn = "check_in_adult_on";
    public const string CheckInAdultOff = "check_in_adult_off";

    public const string ConversationGroup = "Every reply";
    public const string VisionGroup = "Screen and camera glances";
    public const string BackgroundGroup = "After a reply";
    public const string CheckInGroup = "Check-ins";
    public const string HomeGroup = "Smart home";
    public const string AudioModelGroup = "Audio model";

    public static PromptDefinition? Find(string id) => ById.GetValueOrDefault(id);

    /// <summary>Prompts that are the message itself, so they can't be emptied.</summary>
    public static bool Required(string id) => id is GlanceScreen or GlanceCamera or GlanceAttention or BackgroundThink or BackgroundDone or ReminderDue or
            Greeting or SongLyrics or ResearchStep or CheckInDue or DescribePicture or AppGuideOffer or ResearchFound or ResearchSourceCheck;

    public static string Default(string id) =>
        Find(id)?.Default ?? throw new ContractException(ErrorCode.InvalidContract, $"Unknown prompt '{id}'.");
}
