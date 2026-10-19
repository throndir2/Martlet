namespace Martlet.Core.Settings;

public static partial class PromptCatalog
{
    /// <summary>Every prompt: its ID, group, title and help (the labels on Companion › Prompts), its built-in text
    /// (PromptCatalog.Texts.cs) and its placeholders.</summary>
    public static IReadOnlyList<PromptDefinition> All { get; } =
    [
        new(Persona, ConversationGroup, "Persona",
            "Wraps the selected persona's instructions at the start of every reply, screen glance and camera look. It stays the " +
            "same from message to message, so the model's prompt cache can reuse it. Leave it empty to send no persona.",
            DefaultPersonaInstructions,
            ["name", "persona"]),
        new(ReplyLength, ConversationGroup, "Reply length",
            "Closes the instructions of every reply to what you typed or said, after persona and the other instructions.",
            DefaultReplyLengthInstructions, []),
        new(ConversationMoves, ConversationGroup, "Keep the conversation going",
            "Added to every reply to what you typed or said, just before Short first sentence and Reply length: how Martlet keeps " +
            "a chat going (react to your words, add one thing of its own, call back to earlier things, play along with running " +
            "jokes). It is the same from reply to reply, so the model's prompt cache keeps it. Empty it to send nothing.",
            DefaultConversationMovesInstructions, []),
        new(ShortFirstSentence, ConversationGroup, "Short first sentence",
            "Added to every spoken reply just before Reply length while Companion › Replies › Short first sentence is on (the " +
            "default). Martlet's voice says the first sentence as soon as it is written, so a short one lets it start talking " +
            "sooner. It is the same from reply to reply, so the model's prompt cache keeps it. {silent} is the word the model " +
            "answers to stay quiet.",
            DefaultShortFirstSentenceInstructions, ["silent"]),
        new(Listening, ConversationGroup, "Always listening",
            "Added to replies to something the microphone heard. {silent} is the word the model answers to stay quiet.",
            DefaultListeningInstructions,
            ["silent"]),
        new(PcAudio, ConversationGroup, "What this PC plays",
            "Added to replies whose message includes sound playing on the PC (Companion › Listening › Hear what this PC plays). " +
            "{marker} starts each line of it, followed by where it came from when Martlet can tell (a YouTube video in Chrome, a " +
            "game, a voice chat in Discord); {silent} is the word the model answers to stay quiet.",
            DefaultPcAudioInstructions,
            ["marker", "silent"]),
        new(DiscordCall, ConversationGroup, "In your Discord call",
            "Replaces What this PC plays while Martlet is in your own Discord calls (Companion › Discord › Martlet in your " +
            "Discord calls). {marker} starts each line heard from the call; {silent} is the word the model answers to stay quiet.",
            DefaultDiscordCallInstructions,
            ["marker", "silent"]),
        new(Moment, ConversationGroup, "One moment",
            "Added to every reply and every screen or camera glance, the same way each time (so the start of every request stays " +
            "the same): one message may bring several things at once, and Martlet answers them together. {silent} is the word the " +
            "model answers to stay quiet.",
            DefaultMomentInstructions, ["silent"]),
        new(SaidLately, ConversationGroup, "What you said lately",
            "Goes in the notes of what Martlet says on its own while nobody waits for its answer: every screen glance and camera " +
            "look, its remarks on what this PC plays and the replies that bring up finished work, a due reminder or a check-in. " +
            "{said} lists what it said in the last hour (replies to you too, the newest 10, oldest first), each with when it said " +
            "it (\"10:05 PM (12 min ago)\"); {time} is the time now and {silent} the word the model answers to stay quiet. The " +
            "notes go with that one request and are never kept in the conversation, so prompt caches keep working. Replies to " +
            "what you say, type or do never carry it, so their first words never wait for it. Empty it to send nothing.",
            DefaultSaidLatelyInstructions, ["said", "time", "silent"]),
        new(MomentAttention, VisionGroup, "Something wants your attention, with a reply",
            "Goes in the notes of a reply that takes the look Martlet was about to take at something that wants your attention " +
            "(a notification popped up or a taskbar button flashes while it watches your whole screen). {what} says which.",
            DefaultMomentAttentionInstructions,
            ["what", "private"]),
        new(HeardVoice, ConversationGroup, "Your recorded voice",
            "Added to replies when your recording is sent with the transcript (Companion › Listening › Let Thinking hear my voice).",
            DefaultHeardVoiceInstructions,
            []),
        new(HeardVoiceOnly, ConversationGroup, "Your recorded voice, without a transcript",
            "Added to replies when your recording goes straight to Thinking with no transcript (Companion › Listening › When Thinking " +
            "can hear you › Send my voice straight to Thinking). Earlier spoken messages appear as their transcripts.",
            DefaultHeardVoiceOnlyInstructions,
            []),
        new(HeardVoiceDescribed, ConversationGroup, "Your voice, described by the audio model",
            "Added to every reply while an audio model of its own describes your voice (Companion › Listening › Audio model): " +
            "Thinking gets the transcript, never the recording, and the audio model's words about how you sounded come in a note " +
            "(How you sounded). It is the same in every reply, so the start of every request stays the same.",
            DefaultHeardVoiceDescribedInstructions,
            []),
        new(HeardVoiceNote, ConversationGroup, "How you sounded",
            "Goes in the notes of one request only (never kept in the conversation) when the audio model described your voice. " +
            "{when} says which message it is about (\"saying this message\", or \"in what they said before this message\" when " +
            "it came late) and {voice} is what the audio model said. The conversation keeps only a short line after the message, " +
            "such as (voice: sighs, sounds tired). Empty it to send nothing.",
            DefaultHeardVoiceNoteInstructions,
            ["when", "voice"]),
        new(Tools, ConversationGroup, "Tools", "Added when a reply is offered tools: MCP servers', Martlet's own (think_longer, and " +
            "search_conversations when Companion › Memory lets Martlet search past conversations) and the terminal (Companion › Tools).",
            DefaultToolInstructions, []),
        new(ThinkLonger, ConversationGroup, "Thinking longer",
            "Added to every reply offered think_longer (Companion › Replies › Thinking longer, on by default, on a Thinking route " +
            "that does function calling), after the tools prompt. It stays the same from reply to reply while the setting is on.",
            DefaultThinkLongerInstructions, []),
        new(HandedOffTools, ConversationGroup, "Things done after the reply",
            "Added to every reply while Companion › Check-ins › Act on what was said (or another check-in that runs after each " +
            "reply) takes over some of the reply's tools, because a Thinking pool member that calls tools does that work right " +
            "after the reply. {things} names what it does, such as reminders. It stays the same from reply to reply while those " +
            "settings stay. Empty it to send nothing.",
            DefaultHandedOffToolsInstructions, ["things"]),
        new(BackgroundDone, ConversationGroup, "Background work finished",
            "The message of the reply Martlet starts on its own as soon as it is free, once its background work (a think_longer " +
            "task) finished. It stays in the conversation like a message. {results} lists each finished job, how it ended and " +
            "its result.",
            DefaultBackgroundDoneInstructions, ["results"]),
        new(BackgroundDoneNotes, ConversationGroup, "Background work finished, with your message",
            "Goes in the notes of your next message instead, when finished background work hasn't been brought up yet (or " +
            "Thinking longer shares results when you talk next). {results} lists each finished job.",
            DefaultBackgroundDoneNotesInstructions, ["results"]),
        new(ReminderDue, ConversationGroup, "Reminder due",
            "The message of the reply Martlet starts on its own as soon as it is free, once a reminder you asked for is due (the " +
            "reminders tool). It stays in the conversation like a message. {reminders} lists each due reminder: what to remind " +
            "you of, when you asked for it and how late it is.",
            DefaultReminderDueInstructions, ["reminders"]),
        new(ReminderDueNotes, ConversationGroup, "Reminder due, with your message",
            "Goes in the notes of your next message instead, when you talk before Martlet brought a due reminder up, so it fits it " +
            "into its answer. {reminders} lists each due reminder.",
            DefaultReminderDueNotesInstructions, ["reminders"]),
        new(Greeting, ConversationGroup, "Greeting",
            "The message of the reply Martlet starts on its own once it has warmed up after it starts (Settings › Startup and " +
            "closing › Greet me), once each time Martlet starts. It stays in the conversation like a message. {facts} lists what " +
            "Martlet knows then: the day and time (a weekend or a special day such as Halloween), your name when it looks like a " +
            "real name, the end of your last conversation on this PC and a newer Telegram or WhatsApp chat (while Memory keeps " +
            "conversations), reminders that passed while Martlet wasn't running, reminders due in the next 12 hours, a Martlet " +
            "update since it last ran and the apps open on this PC. {silent} is the word the model answers to stay quiet, when " +
            "the character wouldn't greet you.",
            DefaultGreetingInstructions, ["facts", "silent"]),
        new(GreetingNotes, ConversationGroup, "Greeting, with your message",
            "Goes in the notes of your first message instead, when you talk before Martlet greeted you. {facts} lists the same " +
            "facts.",
            DefaultGreetingNotesInstructions, ["facts"]),
        new(Touched, ConversationGroup, "Touched",
            "The message of the short reply Martlet starts on its own when you touch or stroke the desktop character (on zones " +
            "with Martlet notices on, Companion › Touch › Touch zones) or move it around, and say nothing: about 1.2 seconds " +
            "after the last touch, at most once every 4 seconds. {touches} says what you did, such as They slowly stroked down " +
            "from your chest over your stomach to your thighs once. {silent} is the word the model answers to stay quiet.",
            DefaultTouchedInstructions, ["touches", "silent"]),
        new(TouchedNotes, ConversationGroup, "Touched, with your message",
            "Goes in the notes of your next message instead, when you touched the character just before or while you talked or " +
            "typed. {touches} says what you did.",
            DefaultTouchedNotesInstructions, ["touches"]),
        new(TouchedCutIn, ConversationGroup, "Touched, cutting you off",
            "Goes after what you did in both Touched prompts when a touch stopped Martlet while it was talking (Companion › " +
            "Touch › Touch zones › When you touch Martlet while it talks). {said} is a sentence with what Martlet had said " +
            "aloud before it stopped and {answering} one with your message it was answering; each is empty when there is none.",
            DefaultTouchedCutInInstructions, ["said", "answering"]),
        new(CutOff, ConversationGroup, "Cut off: what you hadn't said",
            "Goes in the notes of the next request only (never kept in the conversation) after you stopped Martlet while it was " +
            "talking: you talked over it, pressed Stop or Esc, or a touch stopped it. The conversation keeps only what Martlet " +
            "said aloud, ending with \"—\". {unsaid} is the rest of the reply that it hadn't said (its start, at most " +
            "400 characters). Empty it to send nothing.",
            DefaultCutOffInstructions, ["unsaid"]),
        new(AdultContent, ConversationGroup, "Adult content",
            "Added to the instructions of every reply and screen remark while Companion › Replies › Adult content is on (off by " +
            "default; never in a Discord call, where others can hear), right after the One moment prompt. It allows sexual and " +
            "explicit content with an adult character and says how to take touches on intimate parts. It stays the same from " +
            "reply to reply.",
            DefaultAdultContentInstructions, []),
        new(Singing, ConversationGroup, "Singing",
            "Added to every reply offered sing_song, play_song and stop_singing (while singing is set up in Companion › Voice › " +
            "Singing and the Thinking route does function calling), after Martlet's other tool prompts. It stays the same from " +
            "reply to reply. {silent} is the word the model answers to stay quiet.",
            DefaultSingingInstructions, ["silent"]),
        new(SingingTags, ConversationGroup, "Singing without tools",
            "Added to every reply while singing is set up in Companion › Voice › Singing and the Thinking route does no function " +
            "calling (a model on one of your computers, or a model that refused tools). The reply writes [sing: ...], " +
            "[play_song: ...] and [stop_singing] in place of the song tools; they are never shown or spoken. It stays the same " +
            "from reply to reply. {silent} is the word the model answers to stay quiet. Empty it and Martlet doesn't sing on " +
            "such a route.",
            DefaultSingingTagsInstructions, ["silent"]),
        new(WebResearch, ConversationGroup, "Web research",
            "Added to every reply offered research (while Companion › Deep thinking › Web research is on, with Thinking longer, " +
            "and Deep thinking can think), after the Thinking longer prompt: when to answer from what Martlet knows, when to " +
            "search a guide or report, when to look one fact up quickly and when to research. It stays the same from reply to reply.",
            DefaultWebResearchInstructions, []),
        new(ResearchFound, ConversationGroup, "Web research: found it early",
            "The message of the reply Martlet starts on its own when full or deep research already found what you wanted while it " +
            "goes on for the report. {findings} says which research, what you wanted, what was found and from which pages.",
            DefaultResearchFoundInstructions, ["findings"]),
        new(ResearchFoundNotes, ConversationGroup, "Web research: found it early, with your message",
            "The same in the notes of your message, when you talk before Martlet told you. {findings} is as above.",
            DefaultResearchFoundNotesInstructions, ["findings"]),
        new(AppGuides, ConversationGroup, "App guides",
            "Added to every reply offered read_up_on, search_guide, skip_guide and update_guide_brief (while Companion › App guides " +
            "is on and the Thinking route does function calling), after Martlet's other tool prompts. It stays the same from reply " +
            "to reply.",
            DefaultAppGuidesInstructions, []),
        new(AppGuideNotes, ConversationGroup, "App guide, with your message",
            "Opens the sections of a guide that go in the notes of your message while you ask about a game or app Martlet read " +
            "up on (it is in front, or you name it), and only when they match well. {app} is the app's name. Empty it to send " +
            "no guide sections.",
            DefaultAppGuideNotesInstructions, ["app"]),
        new(AppGuideBrief, ConversationGroup, "App guide brief, with your message",
            "Opens the short brief of a game or app (what it is, its core loop, main mechanics, early tips, common mistakes) that " +
            "a Thinking pool member wrote from its guide. It goes in the notes of your message once a conversation while you talk " +
            "about the app. {app} is the app's name. Empty it to send no brief.",
            DefaultAppGuideBriefInstructions, ["app"]),
        new(AppGuideOffer, ConversationGroup, "App guides: reading up on a game you started",
            "The message of the reply Martlet starts on its own, once a session, when a game (or an app on the App guides list) " +
            "without a guide comes to the front, Read up when I start a game or app is on and Martlet started reading up on it. " +
            "{apps} names the app.",
            DefaultAppGuideOfferInstructions, ["apps"]),
        new(AppGuideOfferNotes, ConversationGroup, "App guides: reading up on a game you started, with your message",
            "The same in the notes of your message, when you talk before Martlet told you. {apps} names the app.",
            DefaultAppGuideOfferNotesInstructions, ["apps"]),
        new(WhileSinging, ConversationGroup, "Said while you were singing",
            "Goes in the notes of what always listening heard while Martlet sings. {song} is the song's title, {where} where the " +
            "song is (\"verse line 4 of 12, 0:22 of 1:00\"), {silent} the word the model answers to stay quiet.",
            DefaultWhileSingingInstructions, ["song", "where", "silent"]),
        new(VoiceTags, ConversationGroup, "Voice sounds and tones",
            "Added to spoken replies when the voice engine understands tags (Chatterbox Turbo: [laugh], [sigh], [whispering]...). " +
            "{engine} is the engine's name, {tags} lists exactly its tags in its own syntax, its non-word sounds and then its tones " +
            "of voice, each group under a line saying where its tags go and each tag on its own line with when to use it, and " +
            "{example} is its first tag.",
            DefaultVoiceTagsInstructions,
            ["engine", "tags", "example"]),
        new(CharacterActions, ConversationGroup, "Character emotes and motions",
            "Added to replies while the desktop character shows and has emotes or motions turned on (Companion › Emotes and " +
            "motions). {tags} lists the ones not already set off by a voice tag, one per line with its When to use hint; {example} " +
            "is the first.",
            DefaultCharacterActionInstructions, ["tags", "example"]),
        new(CharacterShowing, ConversationGroup, "Character emotes showing now",
            "Added to the notes of a message while the desktop character shows lingering emotes (those set to stay on), so the " +
            "reply can turn one off or leave it. It goes with the newest message, never the instructions, so prompt caches keep " +
            "working. {showing} lists them with how long each has shown, such as {glasses} (12 min); {example} is the first one's " +
            "off tag.",
            DefaultCharacterShowingInstructions,
            ["showing", "example"]),
        new(CharacterGaze, ConversationGroup, "Where you look",
            "Added to replies while the desktop character shows and may change where it looks (Companion › Eyes › Where the " +
            "character looks). {usual} is what its eyes usually do (\"follow the user's mouse pointer wherever it goes\"), from " +
            "your choice or the personality; it changes only when that changes, so prompt caches keep working. {tags} lists the " +
            "look tags, one per line with what each does. Empty it and the character keeps its usual gaze.",
            DefaultCharacterGazeInstructions, ["usual", "tags"]),
        new(CharacterLooking, ConversationGroup, "Where you look now",
            "Added to the notes of a message while the character's eyes do something other than their usual because a reply " +
            "changed it. It goes with the newest message, never the instructions, so prompt caches keep working. {looking} is " +
            "what the eyes do now (\"look straight ahead and ignore the pointer\"); {since} is when the reply changed it (\"3 min " +
            "ago\").",
            DefaultCharacterLookingInstructions,
            ["looking", "since"]),
        new(Voices, ConversationGroup, "Who is talking",
            "Introduces the recognized voices block. {label} is the block's marker; the voices follow it.",
            DefaultVoicesInstructions,
            ["label"]),
        new(Lorebook, ConversationGroup, "Lorebook",
            "Introduces the triggered lorebook entries; the entries follow it.",
            DefaultLorebookInstructions,
            []),
        new(MemoryRecall, ConversationGroup, "Memory",
            "Introduces the remembered facts. {label} is the block's marker; the facts follow it.",
            DefaultMemoryRecallInstructions,
            ["label"]),
        new(PastConversations, ConversationGroup, "Past conversations",
            "Introduces excerpts from earlier conversations, which go in the notes of a message that refers to an earlier " +
            "conversation (\"remember when...\", \"what did we talk about yesterday?\") while Martlet keeps a record of " +
            "conversations (Companion › Memory). {label} is the block's marker; today's date and the excerpts follow it.",
            DefaultPastConversationsInstructions,
            ["label"]),
        new(MemoryPeople, ConversationGroup, "Whose memories",
            "Added after the Memory prompt when a remembered fact belongs to someone Martlet knows by voice: such a fact starts " +
            "with their name in brackets, like their messages do.",
            DefaultMemoryPeopleInstructions,
            []),
        new(MemoryBrief, ConversationGroup, "Memory brief, with your message",
            "Opens the memory brief: a short profile of you and the people you talk about, which a Thinking pool member writes " +
            "from what Martlet remembers (Companion › Memory › Memory brief). It goes in the notes of the first message of each " +
            "conversation, never in the instructions, so prompt caches keep working. Empty it to send no brief.",
            DefaultMemoryBriefInstructions, []),
        new(WhilePlaying, ConversationGroup, "While you play",
            "Goes in the notes of a message while Martlet sees a game played on this PC, once while the conversation sent still " +
            "carries it (again after the game changes), never in the instructions, so prompt caches keep working. Lorebook entries " +
            "with the game's name as a keyword are added too, so a lorebook can hold what Martlet should know for one game. {game} " +
            "is the game's name. Empty it to send nothing.",
            DefaultWhilePlayingInstructions, ["game"]),
        new(Notes, ConversationGroup, "Notes with messages",
            "Opens the first notes in the conversation sent. Whatever changes from message to message " +
            "(new lorebook entries and remembered facts, who is talking, smart home results) goes with the message, " +
            "after the conversation so far, and only when it is new, so the start of every request stays the same and the model's " +
            "prompt cache can reuse it. {label} is the notes' marker.",
            DefaultNotesInstructions,
            ["label"]),

        new(CommentaryScreen, VisionGroup, "Screen glance instructions",
            "Instructions for a look at your screen. The chattiness line follows.",
            DefaultCommentaryScreenInstructions,
            ["silent", "private"]),
        new(CommentaryCamera, VisionGroup, "Camera look instructions",
            "Instructions for a look through a camera. The chattiness line follows.",
            DefaultCommentaryCameraInstructions,
            ["silent", "private"]),
        new(ChattinessQuiet, VisionGroup, "Chattiness: quiet", "Closes the glance instructions when vision is quiet.",
            DefaultChattinessQuietInstructions, ["silent"]),
        new(ChattinessNormal, VisionGroup, "Chattiness: normal", "Closes the glance instructions when vision is normal.",
            DefaultChattinessNormalInstructions, ["silent"]),
        new(ChattinessChatty, VisionGroup, "Chattiness: chatty", "Closes the glance instructions when vision is chatty.",
            DefaultChattinessChattyInstructions, ["silent"]),
        new(ChattinessDecides, VisionGroup, "Chattiness: Martlet decides",
            "Closes the glance instructions, and is added to replies to what this PC plays and to your messages while vision is on " +
            "or Martlet hears this PC, when How often it comments is Martlet decides. It stays the same from message to message. " +
            "{quiet}, {normal} and {chatty} are the tags a reply ends with to switch the level (at the end, so the first words " +
            "aren't held back; never shown or spoken); {silent} is the word for staying quiet.",
            DefaultChattinessDecidesInstructions, ["silent", "quiet", "normal", "chatty"]),
        new(ChattinessNow, VisionGroup, "Chattiness right now",
            "Goes in the notes of a message while Martlet decides how chatty it is, when the conversation's notes don't already " +
            "say the level (it starts at normal and changes when a reply switches it). {level} is quiet, normal or chatty.",
            DefaultChattinessNowInstructions, ["level"]),
        new(GlanceScreen, VisionGroup, "Screen glance message",
            "The message sent with each screenshot. {app} is the program in front by name (such as Google Chrome), with " +
            "(full screen) when its window fills its monitor, a borderless one too; {title} is the active window's title; " +
            "{changed} says how much changed since Martlet's last look and how long ago it was (\"Only small things changed " +
            "since your last look 3 min ago.\"), then how long nobody used the keyboard or mouse once it is a minute or more. " +
            "While nothing changed since the last look, Martlet doesn't look at all. What Martlet said lately goes in its notes " +
            "(What you said lately).",
            DefaultGlanceScreenInstructions,
            ["app", "title", "changed", "silent"]),
        new(GlanceCamera, VisionGroup, "Camera look message",
            "The message sent with each camera image. {title} is the camera's name; {changed} says how much changed since " +
            "Martlet's last look and how long ago it was (\"It changed since your last look 2 min ago.\"). While nothing changed " +
            "since the last look, Martlet doesn't look at all. What Martlet said lately goes in its notes (What you said lately).",
            DefaultGlanceCameraInstructions,
            ["title", "changed", "silent"]),
        new(GlanceAttention, VisionGroup, "Notification glance message",
            "The message sent with the screenshot Martlet takes right away when a notification pops up or a taskbar button flashes " +
            "while it watches your whole screen. {what} says which; {app} is the program in front (as in the Screen glance " +
            "message); {title} is the active window's title. What Martlet said lately goes in its notes (What you said lately).",
            DefaultGlanceAttentionInstructions,
            ["what", "app", "title", "silent", "private"]),
        new(GlanceLook, VisionGroup, "Where the character looks",
            "Added to screen glances while Companion › Vision › Glances at your screen is Martlet decides and the character " +
            "shows, so the Thinking model can turn the character's eyes to a part of the picture. {tags} lists the nine look tags, " +
            "one per line with where each looks; {silent} is the word for staying quiet. Empty it and only what changes on screen " +
            "draws the character's eyes.",
            DefaultGlanceLookInstructions, ["tags", "silent"]),
        new(SeenWithMessage, VisionGroup, "Screen with your message",
            "Added to replies while vision is on: the newest picture of what Martlet watches goes with what you type or say. " +
            "{source} says what the picture shows (your active window, your whole screen or a camera). It stays the same when " +
            "you switch windows, so the model's prompt cache keeps it: the program in front and its window's title go in the " +
            "message's notes (Active app with your message) when they changed since the conversation's latest [Screen] line.",
            DefaultSeenWithMessageInstructions,
            ["source", "private"]),
        new(SeenApp, VisionGroup, "Active app with your message",
            "Goes with your message when its picture shows your screen and the program in front or its window changed since " +
            "the conversation's latest [Screen] line, in the notes that are sent but not kept (the conversation keeps the " +
            "message's [Screen] line instead), so the instructions stay the same when you switch windows and nothing is added " +
            "while you stay in one window. {app} is the program in front by name (such as Google Chrome), with (full screen) " +
            "when its window fills its monitor, a borderless one too; {title} is its window's title. Empty it and replies " +
            "aren't told which app is in front.",
            DefaultSeenAppInstructions,
            ["app", "title"]),
        new(SeenTag, VisionGroup, "What you saw",
            "Added to every screen glance and camera look, and to replies whose message comes with a picture, after their own " +
            "instructions; it never changes, so the instructions stay the same. The reply ends with [seen: ...]: a few words on " +
            "what the picture shows, never shown or spoken. Martlet keeps them in the conversation (as a [Screen] or [Camera] " +
            "line) instead of the picture, which is never kept. {silent} is the word for staying quiet. Empty it and the " +
            "conversation keeps only where Martlet looked.",
            DefaultSeenTagInstructions,
            ["silent", "private"]),
        new(SeenDescribed, VisionGroup, "Pictures as words",
            "Added to every reply and look while an image model of its own describes pictures for the Thinking model (Companion " +
            "› Vision › Image model), in place of Screen with your message and What you saw. It never changes, so the model's " +
            "prompt cache keeps it. The image model's description goes in the notes of the message it came with, or in a look's " +
            "message (What the image model saw).",
            DefaultSeenDescribedInstructions,
            ["private"]),
        new(SeenDescribedNote, VisionGroup, "What the image model saw",
            "The image model's description of the newest picture: in the notes of your message (sent but not kept; the " +
            "conversation keeps a [Screen] line made from its first line instead), or in a look's message. {source} says what " +
            "the picture shows (your active window, your whole screen or a camera); {description} is the image model's answer. " +
            "A reply takes it only when it is ready and never waits for it.",
            DefaultSeenDescribedNoteInstructions,
            ["source", "description"]),
        new(DescribePicture, VisionGroup, "Image model: describe the picture",
            "The instructions an image model of its own (Companion › Vision › Image model) gets with each picture it describes " +
            "for the Thinking model: while you talk or type, and for each look. Martlet's message with the picture says what it " +
            "shows, the program in front and its window's title, and the last few lines of the conversation. The first line of " +
            "the answer stays in the conversation as what Martlet saw. {private} is the Personal content prompt.",
            DefaultDescribePictureInstructions, ["private"]),
        new(ReadOnScreen, VisionGroup, "Text on screen",
            "Added at the end of a screen glance, and of a reply that takes a look, while Companion › Reading reads the text on " +
            "your screen. {text} is the text read from that screenshot, one line each, top to bottom. It never goes with what " +
            "you type or say.",
            DefaultReadOnScreenInstructions,
            ["text"]),
        new(ScreenDigest, VisionGroup, "Screen summary over time",
            "Sent in the background, never on the live conversation's route, while Screen summary over time is on: one picture " +
            "made of {count} small screenshots from the last {seconds} seconds ({panels} says where each is, when it was " +
            "taken and the program in front), then the text read on them. The one or two lines it answers go with your next " +
            "message as a note; [{silent}] means nothing changed. A reply never waits for it.",
            DefaultScreenDigestInstructions,
            ["count", "seconds", "panels", "silent", "private"]),
        new(PersonalPrivate, VisionGroup, "Personal content: private",
            "Fills {private} in the screen and camera prompts while Companion › Replies › Personal content is Keep private (the " +
            "default), so Martlet keeps messages, emails and numbers it sees to itself.",
            DefaultPersonalPrivateInstructions, []),
        new(PersonalShared, VisionGroup, "Personal content: shared",
            "Fills {private} in the screen and camera prompts while Companion › Replies › Personal content is Read my messages " +
            "or Everything, so Martlet may read and talk about the messages, chats and emails it sees.",
            DefaultPersonalSharedInstructions, []),

        new(MemoryCapture, BackgroundGroup, "Remembering",
            "Asks the Thinking model what to remember after each reply. Martlet reads the REMEMBER, UPDATE and FORGET lines it answers, " +
            "and the WORDS line after a REMEMBER or UPDATE line as the fact's hidden search words; " +
            "{nothing} is the word for no change.",
            DefaultMemoryCaptureInstructions, ["nothing"]),
        new(MemoryCaptureGame, BackgroundGroup, "Remembering while you play",
            "Added to Remembering while Martlet sees a game played on this PC, so a fact about that game is kept for it and isn't " +
            "brought up in other games. {game} is the game's name. Martlet reads REMEMBER HERE (this game only), REMEMBER GAMES " +
            "(any game) and REMEMBER EVERYWHERE; a REMEMBER line without one counts as HERE.",
            DefaultMemoryCaptureGameInstructions, ["game"]),
        new(MemoryUpkeep, BackgroundGroup, "Tidying memory",
            "Sent in the background to a free Thinking pool member, never to the conversation's own model: groups of remembered " +
            "facts that may repeat or contradict each other, and facts without search words. Martlet reads the SAME (merge), " +
            "OUTDATED (keep the old fact as history) and WORDS (hidden search words) lines it answers; {nothing} is the word for " +
            "no change.",
            DefaultMemoryUpkeepInstructions, ["nothing"]),
        new(MemoryBriefWriting, BackgroundGroup, "Writing the memory brief",
            "Asks a Thinking pool member (or the Thinking model in the cloud, once the conversation is quiet) to write the memory " +
            "brief from the remembered facts, which follow it, each with whose it is. Martlet writes it again when facts change. " +
            "Empty it and Martlet writes no brief.",
            DefaultMemoryBriefWritingInstructions, []),
        new(VoiceNaming, BackgroundGroup, "Learning names",
            "Sent after a reply, in a Thinking request of its own (a Thinking pool member that takes naming jobs, else the " +
            "conversation's Thinking model once the conversation is quiet): who the people speaking are. Martlet reads the NAME, " +
            "CALL, NOT and SAME lines it answers about recognized voices, and the SPOKE line about a voice it wasn't sure of (a " +
            "label only), and never gives a voice the companion's own names; {nothing} is the word for none.",
            DefaultVoiceNamingInstructions, ["nothing"]),
        new(CharacterActionNaming, BackgroundGroup, "Naming character emotes",
            "Asks the Thinking model what each of a character model's emotes and motions is (Companion › Emotes and " +
            "motions › Name them with Thinking; also once for each new model). The numbered list follows it; Martlet reads the " +
            "\"<number>: tag | cue | when\" and SKIP lines. {cues} lists the voice sounds and tones an emote can follow.",
            DefaultCharacterActionNamingInstructions, ["cues"]),
        new(CharacterWriting, BackgroundGroup, "Writing a character's personality",
            "Asks the Thinking model to write the personality of a character you name in Find a character (Companion › Character). " +
            "The character's name and your notes follow it.",
            DefaultCharacterWritingInstructions, []),
        new(BackgroundThink, BackgroundGroup, "Thinking longer: the task",
            "The message of a background think (think_longer). It continues the conversation exactly as the reply that started it " +
            "sent it, so the model's prompt cache is reused. {task} is the task Martlet gave; {reason} is why, on a line of its own " +
            "when Martlet said.",
            DefaultBackgroundThinkInstructions, ["task", "reason"]),
        new(SongLyrics, BackgroundGroup, "Singing: writing the song",
            "The task of a song's first step (sing_song without lyrics): a background think writes the title, style, tempo, key " +
            "and lyrics, which Martlet reads from its TITLE, STYLE, BPM, KEY and LYRICS lines. {about} is what the song is about, " +
            "{style} the style asked for (a line of its own when there is one), {seconds} its length and {lines} about how many " +
            "lines fit.",
            DefaultSongLyricsInstructions, ["about", "style", "seconds", "lines"]),
        new(ResearchStep, BackgroundGroup, "Web research: each step",
            "The task of each step of a research job: where Deep thinking thinks, the model reads its notes and what is new since " +
            "its last step, rewrites its notes (NOTES), says what it found once the pages answer it (FOUND, which the watcher takes " +
            "to tell you early or to end a quick look-up) and answers with SEARCH, READ or the report (TITLE, SUMMARY and REPORT " +
            "lines), which Martlet reads. {topic} and {find} are what the user wants researched, {depth} how hard to look (a quick " +
            "look-up, full or deep research), {notes} the notes so far, {sources} the searches so far, the latest results not read " +
            "yet and the new pages, {step} and {steps} where it is, and {last} a line asking for the report (or, for a quick " +
            "look-up, the answer) on the last step.",
            DefaultResearchStepInstructions, ["topic", "find", "depth", "notes", "sources", "step", "steps", "last"]),
        new(ResearchSourceCheck, BackgroundGroup, "Web research: checking the sources",
            "The task of each request of a report's source check, after the report is ready: a Thinking pool member reads up to " +
            "six claims of the report, each with the page it cites and the parts of that page that match it best, and answers " +
            "SUPPORTED, PARTLY or NOT SUPPORTED for each, which Martlet reads. {claims} are the numbered claims.",
            DefaultResearchSourceCheckInstructions, ["claims"]),

        new(CheckIn, CheckInGroup, "Check-ins: instructions",
            "The instructions of every check-in (Companion › Check-ins): a Thinking pool member gets them with each check, which " +
            "follows as the message.",
            DefaultCheckInInstructions, []),
        new(CheckInEmotes, CheckInGroup, "Check-in: lingering emotes",
            "What Lingering emotes checks. Every check-in's prompt may name any of these placeholders: {name} the character's " +
            "name, {time} the day and time, {adult} whether Adult content is on (Check-in: adult content on or off), " +
            "{conversation} the end of the conversation, {emotes} the lingering emotes with their " +
            "hints and how long each has shown, {example} the first one's tag, {looking}, {usual} and {since} the gaze a reply " +
            "chose, {persona}, {work}, {screen}, {sound}, {presence}, {said} what it said in the last hour, {replies} its last " +
            "replies and {callbacks} the callback list. Check-ins: each check adds the facts ticked that the prompt doesn't name and the answer format (OFF " +
            "lines or KEEP for this one).",
            DefaultCheckInEmotesInstructions, ["name", "emotes", "conversation"]),
        new(CheckInGaze, CheckInGroup, "Check-in: where the character looks",
            "What Where the character looks checks. {looking} is what the eyes do now, {since} when the reply chose it (\"12 min " +
            "ago\"), {usual} what they usually do and {conversation} the end of the conversation. Check-ins: each check adds the " +
            "answer format (USUAL or KEEP for this one).",
            DefaultCheckInGazeInstructions, ["name", "since", "looking", "usual", "conversation"]),
        new(CheckInPromises, CheckInGroup, "Check-in: promises",
            "What Promises checks. {work} lists the reminders set and the background work started or finished in this " +
            "conversation. Check-ins: each check adds the answer format (a REMIND: line goes in the notes of the next message; " +
            "OK changes nothing).",
            DefaultCheckInPromisesInstructions, ["name", "conversation", "work"]),
        new(CheckInCharacter, CheckInGroup, "Check-in: staying in character",
            "What Staying in character checks. {persona} is the active personality and {replies} the last replies, oldest first. " +
            "Check-ins: each check adds the answer format (a REMIND: line or OK).",
            DefaultCheckInCharacterInstructions, ["name", "persona", "replies"]),
        new(CheckInRepeats, CheckInGroup, "Check-in: saying the same things",
            "What Saying the same things checks. {said} lists what the character said in the last hour (the newest 10, oldest " +
            "first), each with when it said it (\"10:05 PM (12 min ago)\"). Check-ins: each check adds the answer format (a " +
            "REMIND: line or OK).",
            DefaultCheckInRepeatsInstructions, ["name", "said"]),
        new(CheckInTouches, CheckInGroup, "Check-in: describe touches",
            "What Describe touches asks right after you touch the character. {touches} is what you did to it in the last 10 " +
            "minutes, in the words of the touch reaction, {conversation} the end of the conversation, {persona} its personality, " +
            "{emotes} the emotes it shows and {adult} whether Adult content is on. Check-ins: each check adds the answer format " +
            "(a description that goes with the next reply, or nothing).",
            DefaultCheckInTouchesInstructions, ["name", "touches", "conversation", "persona", "emotes", "adult"]),
        new(CheckInWelcome, CheckInGroup, "Check-in: welcome back",
            "What Welcome back checks, when the user comes back to the PC after 10 minutes or more away. {away} says how long " +
            "they were away and what happened meanwhile (what Martlet said, background work that finished, reminders that came " +
            "due), and {activity} what they seem to be doing now. Check-ins: each check adds the answer format (a SAY: line or OK).",
            DefaultCheckInWelcomeInstructions, ["name", "away", "activity"]),
        new(CheckInUnanswered, CheckInGroup, "Check-in: unanswered question",
            "What Unanswered question checks, when Martlet's last remark asked something and the user, at the PC, didn't answer " +
            "for a few minutes. Check-ins: each check adds the answer format (a SAY: line or OK).",
            DefaultCheckInUnansweredInstructions, ["name", "conversation", "said", "activity"]),
        new(CheckInAside, CheckInGroup, "Check-in: talking to someone else",
            "What Talking to someone else checks right after always listening hears the user. {heard} lists what it heard in the " +
            "last 10 minutes (when, who spoke, other voices, talking over each other, lines this PC played, and whether Martlet " +
            "answered or stayed quiet) and whether Martlet holds back now, {conversation} the end of the conversation, {activity} " +
            "what the user does on this PC (a call or voice chat) and {people} the voices heard lately. Check-ins: each check adds " +
            "the answer format (an ASIDE: line, BACK or OK).",
            DefaultCheckInAsideInstructions, ["name", "heard", "conversation", "activity", "people"]),
        new(CheckInAsideNote, CheckInGroup, "Check-in: holding back while you talk to someone else",
            "Goes in the notes of every message while Martlet holds back because a check-in said the user talks to someone else, " +
            "never in the conversation or its instructions, so prompt caches keep working. {about} is who the user talks to and " +
            "how, in a few words, and {silent} the word Martlet answers to stay quiet.",
            DefaultCheckInAsideNoteInstructions, ["about", "silent"]),
        new(CheckInOthers, CheckInGroup, "Check-in: someone else is here",
            "What Someone else is here checks, when Martlet hears a voice that isn't the user's. {people} lists the voices heard " +
            "lately (names only, never what they said). Check-ins: each check adds the answer format (a REMIND: line or OK).",
            DefaultCheckInOthersInstructions, ["name", "people"]),
        new(CheckInActions, CheckInGroup, "Check-in: act on what was said",
            "What Act on what was said checks right after each reply. {exchange} is the latest exchange (what the user said and " +
            "the reply), or every exchange since it last ran, and {work} the reminders set and the background work started or " +
            "finished. It changes nothing itself: the tools of the sets it may use do (such as reminders).",
            DefaultCheckInActionsInstructions, ["name", "exchange", "work"]),
        new(CheckInAhead, CheckInGroup, "Check-in: think ahead",
            "What Think ahead asks right after each reply, while the user hasn't answered yet. {persona} is the active " +
            "personality, {conversation} the end of the conversation and {callbacks} the callback list (what Callback list keeps, " +
            "each with when). Check-ins: each check adds the answer format (a PLAN: line with the character's own thoughts ahead " +
            "and the one move it makes, which goes with the next reply, or OK).",
            DefaultCheckInAheadInstructions, ["name", "persona", "conversation", "callbacks"]),
        new(CheckInCallbacks, CheckInGroup, "Check-in: callback list",
            "What Callback list asks right after each reply. {callbacks} is the list now (open topics, running jokes, the user's " +
            "words and facts worth bringing back later, each with when it came up) and {exchange} what was said since it last ran. " +
            "Check-ins: each check adds the answer format (the whole new list in CALLBACK: lines, KEEP or NONE). Think ahead and " +
            "Quiet moments read the list.",
            DefaultCheckInCallbacksInstructions, ["name", "callbacks", "exchange"]),
        new(CheckInLull, CheckInGroup, "Check-in: quiet moments",
            "What Quiet moments checks when the conversation has been quiet for a few minutes and the user is still at the PC. " +
            "{callbacks} is the callback list, {said} what Martlet said lately and {activity} what the user seems to be doing. " +
            "Check-ins: each check adds the answer format (a SAY: line or OK).",
            DefaultCheckInLullInstructions, ["name", "persona", "conversation", "callbacks", "said", "activity"]),
        new(CheckInReactions, CheckInGroup, "Check-in: how I react",
            "What How I react checks. {persona} is the active personality, {conversation} the end of the conversation and " +
            "{touches} how the user touched the character lately. It changes nothing itself: the Touch reactions tools it calls " +
            "(read_touch_reactions, set_touch_mood, change_touch_feeling, change_zone_reactions and undo_touch_change) do, " +
            "each change bounded and lasting a few hours at most.",
            DefaultCheckInReactionsInstructions, ["name", "persona", "conversation", "touches"]),
        new(CheckInCustom, CheckInGroup, "Check-ins: each check",
            "Wraps the prompt of every check-in, built-in or your own. {task} is the check-in's prompt with its placeholders " +
            "filled in, {facts} the facts ticked that the prompt doesn't name and what was gathered for this run, {time} the day " +
            "and time, and {answer} the answer format of what happens with the answer: OFF lines or KEEP (turn off emotes), USUAL " +
            "or KEEP (move the eyes), a REMIND: line or OK (a reminder for the next reply), a SAY: line or OK (Martlet brings it " +
            "up), a KNOW: line or OK (adds to what Martlet knows), a PLAN: line or OK (thinks ahead for the next reply), an ASIDE: " +
            "line, BACK or OK (holds Martlet back while you talk to someone else), CALLBACK: lines, KEEP or NONE (keeps the " +
            "callback list). Emptied, " +
            "each check sends only its prompt and facts, so the " +
            "prompt must then ask for the answer format itself.",
            DefaultCheckInCustomInstructions, ["task", "facts", "time", "answer"]),
        new(CheckInNote, CheckInGroup, "Check-in: reminder for the next reply",
            "Goes in the notes of the next message when a check-in answers with a REMIND: line, once, never in the instructions, " +
            "so prompt caches keep working. {reminder} is that line's text.",
            DefaultCheckInNoteInstructions, ["reminder"]),
        new(CheckInContext, CheckInGroup, "Check-in: adds to what Martlet knows",
            "Goes in the notes of the next message when a check-in that adds to what Martlet knows answers with a KNOW: line, once " +
            "and only within a few minutes, never in the conversation or its instructions, so prompt caches keep working. " +
            "{context} is that line's text: a short description of what is happening, which the reply may draw on.",
            DefaultCheckInContextInstructions, ["context"]),
        new(CheckInPlan, CheckInGroup, "Check-in: thoughts ahead for the next reply",
            "Goes in the notes of the next message when a check-in that thinks ahead answers with a PLAN: line, once and only " +
            "while no newer exchange ended, never in the conversation or its instructions, so prompt caches keep working. " +
            "{plan} is that line's text: the character's own thoughts about what the user will say next and what it means to " +
            "say then.",
            DefaultCheckInPlanInstructions, ["plan"]),
        new(CheckInDue, CheckInGroup, "Check-in: brought up on its own",
            "The message of the reply Martlet starts on its own as soon as it is free, when one of your own check-ins that brings " +
            "things up answers with a SAY: line. {items} is what it said to bring up.",
            DefaultCheckInDueInstructions, ["items"]),
        new(CheckInDueNotes, CheckInGroup, "Check-in: brought up, with your message",
            "The same in the notes of your message, when you talk first. {items} is what it said to bring up.",
            DefaultCheckInDueNotesInstructions, ["items"]),
        new(CheckInAdultOn, CheckInGroup, "Check-in: adult content on",
            "Fills {adult} in a check-in's prompt while Companion › Replies › Adult content is on, so the check-in may be " +
            "explicit, as the replies may.",
            DefaultCheckInAdultOnInstructions, []),
        new(CheckInAdultOff, CheckInGroup, "Check-in: adult content off",
            "Fills {adult} in a check-in's prompt while Companion › Replies › Adult content is off, so the check-in stays " +
            "non-explicit.",
            DefaultCheckInAdultOffInstructions, []),

        new(HomeWrap, HomeGroup, "Smart home status",
            "Wraps every smart home note below. {label} is the block's marker; {body} is the note.",
            DefaultHomeWrapInstructions,
            ["label", "body"]),
        new(HomeDone, HomeGroup, "Home: done", "Home Assistant carried out the request. {details} is its report and the devices it changed.",
            DefaultHomeDoneInstructions, ["details"]),
        new(HomeAnswer, HomeGroup, "Home: answer", "Home Assistant answered a question about the home. {details} is its report.",
            DefaultHomeAnswerInstructions, ["details"]),
        new(HomeFailed, HomeGroup, "Home: couldn't do it", "Home Assistant understood but couldn't carry it out. {details} is its report.",
            DefaultHomeFailedInstructions, ["details"]),
        new(HomeTools, HomeGroup, "Home: tools", "When replies use Home Assistant's own tools. {locks} is one of the two lines below.",
            DefaultHomeToolsInstructions, ["locks"]),
        new(HomeLocksConfirm, HomeGroup, "Home: locks ask first", "Fills {locks} when locks, doors and similar devices are allowed.",
            DefaultHomeLocksConfirmInstructions, []),
        new(HomeLocksOff, HomeGroup, "Home: locks off", "Fills {locks} when locks, doors and similar devices are turned off.",
            DefaultHomeLocksOffInstructions, []),
        new(HomeNotRecognized, HomeGroup, "Home: not a command", "Home Assistant didn't recognize the words as a home command.",
            DefaultHomeNotRecognizedInstructions, []),
        new(HomeBlocked, HomeGroup, "Home: blocked", "The words mention a lock, door or similar device that Smart home settings don't allow.",
            DefaultHomeBlockedInstructions, []),
        new(HomeDeclined, HomeGroup, "Home: declined", "The user declined to confirm a request about a lock, door or similar device.",
            DefaultHomeDeclinedInstructions, []),
        new(HomeUnreachable, HomeGroup, "Home: unreachable", "Martlet couldn't reach Home Assistant.",
            DefaultHomeUnreachableInstructions, []),

        new(VoiceDescription, AudioModelGroup, "Describing your voice",
            "The instructions the audio model gets with the recording of each thing you say, while an audio model of its own " +
            "describes your voice for Thinking (Companion › Listening › Audio model). The last lines of the conversation follow, " +
            "so it knows what matters. Its first line goes after your message in the conversation (How you sounded); none means " +
            "nothing stood out. Empty it and the audio model gets no recordings of your voice.",
            DefaultVoiceDescriptionInstructions,
            [])
    ];

    // After All: static fields in one file start in the order they are written.
    private static readonly Dictionary<string, PromptDefinition> ById = All.ToDictionary(p => p.Id, StringComparer.Ordinal);
}
