using System.IO;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Creations;
using Martlet.Core.Pictures;
using Martlet.Core.Settings;
using Martlet.Core.Singing;
using Martlet.Providers;
using Martlet.Providers.Pictures;
using Martlet.Memory;

namespace Martlet.Desktop;

internal sealed partial class LiveConversationController
{
    /// <summary>The live conversation's own tools: the set a reply offers (<see cref="BuiltIns"/>, always the same ones in the same
    /// order while their settings stay, so the start of every request stays the same) and what each one does: think_longer and
    /// cancel_thinking, the song tools, draw_picture, body_motion, manage_memories and find_memories, reminders,
    /// search_conversations, the creations tools, call_on_discord and set_camera_background. The check-ins that take tools over
    /// after a reply run the same code. It is nested in the controller, so it reads the conversation's state through
    /// <c>owner</c>; the controller's members that other code calls forward to it.</summary>
    internal sealed class ConversationTools
    {
        private readonly LiveConversationController owner;

        internal ConversationTools(LiveConversationController owner)
        {
            this.owner = owner;
            songCredentials = new(() => Volatile.Read(ref songAuthorization));
        }

        // A song's lyrics writer on the Deep thinking place it holds while it writes.
        private sealed record LyricsWriter(BackgroundThink Writer, (string Thinking, string Deep)? Beside, BackgroundPlaceLease Place);

        private readonly ConversationCredentialSource songCredentials;
        internal ConversationRuntime? songRuntime;
        private ICredentialAuthority? songAuthorization;

        /// <summary>think_longer and cancel_thinking as a reply carries them while Thinking longer is on and the pool can think, else
        /// none. How many think at once comes from the configured members alone (<see cref="DeepPool"/>: every computer counted as
        /// online), never from what answers or is busy now, so the start of every request stays the same for prompt caches while
        /// computers come and go.</summary>
        internal IReadOnlyList<TextToolDefinition> ReplyThinkTools(LiveConversationConfiguration configured) =>
            configured.OffersThinkLonger && owner.DeepPool(configured) is { Plan.Available: true } pool
                ? ThinkLonger.Definitions(configured.ThinkLonger, ThinkLonger.Slots(ThinkLonger.Places(pool))) : [];

        /// <summary>Martlet's own tools for one reply, always the same ones in the same order while their settings stay, so the start
        /// of every request stays the same: think_longer and cancel_thinking while Thinking longer is on (with the Thinking longer
        /// prompt), research while Web research is on too (with its prompt), then the song tools while singing is set up, then draw_picture while pictures are set up (Companion › Picture making),
        /// then search_conversations while the owner lets Martlet search the record of conversations (Companion › Memory,
        /// off by default), then list_creations and perform_creation while any kind of creation for the tools is registered (CreationRegistry,
        /// docs/CREATIONS.md), then manage_memories while memory is on, then reminders, call_on_discord and set_camera_background
        /// while they apply, then the App guides tools, and body_motion last while motions are set up and the character is a VRM. Last, the tools a check-in that runs after each exchange takes over (<see cref="HandedOffReplyTools"/>)
        /// are left out, and the Things done after the reply prompt says so. Null when there are none.</summary>
        internal BuiltInTools? BuiltIns(LiveConversationOperation operation, LiveConversationConfiguration configured, Guid conversation)
        {
            var own = new List<(TextToolDefinition, Func<TextToolCall, CancellationToken, ValueTask<ConversationToolResult>>)>();
            string? guidance = null;
            // think_longer and research handed off to the actions check-in: the reply keeps cancel_thinking and only says it'll get
            // back to the user; the check-in starts the work after the reply.
            var handedOff = owner.HandedOffReplyTools(configured);
            if (ReplyThinkTools(configured) is { Count: > 0 } thinkTools)
            {
                var settings = configured.ThinkLonger;
                if (handedOff.Contains(ThinkLonger.Name)) guidance = BackgroundWorkTools.ReplyGuidance;
                else
                {
                    own.Add((thinkTools[0], (call, token) => ThinkLongerAsync(operation, configured, call)));
                    guidance = ThinkLonger.Instructions(settings, configured.Prompts);
                }
                own.Add((thinkTools[1], (call, token) => ValueTask.FromResult(CancelThinking(call))));
            }
            // research while Web research is on (Companion › Deep thinking, on by default) and Deep thinking can think.
            if (owner.OffersResearch(configured))
            {
                if (!handedOff.Contains(WebResearch.Name))
                {
                    own.Add((WebResearch.Definition, (call, token) => ValueTask.FromResult(owner.Research(operation, configured, call))));
                    guidance = Join(guidance, WebResearch.Instructions(configured.Prompts));
                }
                else if (!handedOff.Contains(ThinkLonger.Name)) guidance = Join(guidance, BackgroundWorkTools.ReplyGuidance);
            }
            // sing_song, play_song and stop_singing while singing is set up (with the Singing prompt). While the After each exchange
            // check-in takes sing_song and play_song over (HandedOffReplyTools), the reply keeps stop_singing and a short line instead.
            var afterReply = new HashSet<string>(StringComparer.Ordinal);
            if (owner.singing is { Offered: true } && configured.SupportsTools)
            {
                var songs = SongTools.Definitions;
                if (handedOff.Contains(songs[0].Name)) afterReply.Add(songs[0].Name);
                else own.Add((songs[0], (call, token) => ValueTask.FromResult(SingSong(operation, configured, call))));
                if (handedOff.Contains(songs[1].Name)) afterReply.Add(songs[1].Name);
                else own.Add((songs[1], (call, token) => PlaySongAsync(configured, call, token)));
                own.Add((songs[2], (call, token) => ValueTask.FromResult(StopSinging(call))));
                if (afterReply.Count == 0) guidance = Join(guidance, SongTools.Instructions(configured.Prompts));
            }
            // draw_picture while pictures are set up (Companion › Picture making).
            if (configured.SupportsTools && owner.dataDirectory is not null && PictureClient.IsSetUp(owner.dataDirectory))
            {
                if (handedOff.Contains(PictureTools.DrawName)) afterReply.Add(PictureTools.DrawName);
                else own.Add((PictureTools.Definition, (call, token) => ValueTask.FromResult(DrawPicture(operation, configured, call))));
            }
            if (configured.SupportsTools && owner.history?.Searchable(configured.Memory) == true)
                own.Add((PastConversations.Definition, (call, token) => SearchConversationsAsync(call, conversation, configured.CharacterName, token)));
            var kinds = owner.Creations.ToolKinds;
            if (kinds.Count > 0 && configured.SupportsTools && owner.dataDirectory is not null)
            {
                var definitions = CreationTools.Definitions(kinds);
                own.Add((definitions[0], (call, token) => ValueTask.FromResult(ListCreations(call))));
                if (handedOff.Contains(definitions[1].Name)) afterReply.Add(definitions[1].Name);
                else own.Add((definitions[1], PerformCreationAsync));
            }
            guidance = Join(guidance, CreationsCheckIn.ReplyGuidance(afterReply, singing: owner.singing is { Offered: true } && configured.SupportsTools));
            // manage_memories while memory is on: last, so the tools before it start every request the same as before it existed.
            // While the Memory check-in tool set takes it over, the reply gets the read-only find_memories in its place instead.
            if (configured.SupportsTools && owner.memory is not null && configured.Memory is { Enabled: true } remembered)
            {
                if (handedOff.Contains(MemoryTools.Name))
                {
                    own.Add((MemoryTools.FindDefinition, (call, token) => ManageMemoriesAsync(remembered.ConfigurationRevision,
                        MemoryTools.WithAction(call.ArgumentsJson, "find"), operation.Heard?.Speaker?.Voice, MemoryTools.FindName, null, token)));
                    guidance = Join(guidance, MemoryTools.HandedOffGuidance);
                }
                else
                    own.Add((MemoryTools.Definition, (call, token) => ManageMemoriesAsync(remembered.ConfigurationRevision, call.ArgumentsJson,
                        operation.Heard?.Speaker?.Voice, MemoryTools.Name, null, token)));
            }
            // reminders after it, on a PC that keeps reminders (always the same text, so the start of every request stays the same).
            // While an After each exchange check-in takes it over, the reply only lists them (list_reminders) in the same place.
            if (configured.SupportsTools && owner.RemindersTool is not null)
            {
                if (handedOff.Contains(Reminders.ToolName))
                {
                    own.Add((Reminders.ListDefinition, (call, token) => ListRemindersAsync(token)));
                    guidance = Join(guidance, Reminders.AfterReply);
                }
                else own.Add((Reminders.Definition, (call, token) => ReplyRemindAsync(call, token)));
            }
            // call_on_discord after them while Martlet can call a Discord friend (saved choices only, so it doesn't come and go with the
            // connection). While the After each exchange check-in takes it over, the reply only says it will call.
            if (configured.SupportsTools && owner.DiscordCaller is { CanCall: true } caller)
            {
                if (handedOff.Contains(DiscordCallTool.Name)) guidance = Join(guidance, DiscordCallTool.AfterReply);
                else own.Add((DiscordCallTool.Definition, (call, token) => CallOnDiscordAsync(caller, call, token)));
            }
            // set_camera_background last, while Martlet is in the owner's Discord calls (the saved choice only).
            if (configured.SupportsTools && owner.CallCamera is { Offered: true } camera)
            {
                if (handedOff.Contains(CameraBackgroundTool.Name)) guidance = Join(guidance, CameraBackgroundTool.AfterReply);
                else own.Add((CameraBackgroundTool.Definition, (call, token) => SetCameraBackgroundAsync(operation, configured, camera, call, token)));
            }
            // read_up_on, search_guide and skip_guide after them while App guides are on (Companion › App guides, on by default), so
            // the tools before them start every request the same as before App guides existed.
            owner.AddGuideTools(own, ref guidance, operation, configured);
            // body_motion last, while motions are set up and the chosen character is a VRM (saved choices only), so switching between
            // a VRM and a Live2D character changes only the end of the tools, and nothing changes without motions.
            if (configured.SupportsTools && owner.Motions is { Offered: true })
                own.Add((MotionTools.Definition, (call, token) => BodyMotionAsync(call, token)));
            // The tools a check-in that runs after each exchange takes over go to it instead, with one line that says so.
            var handed = owner.HandOffs(configured);
            return WithoutHandedOff(new(own, guidance), handed, CheckIns.HandOffGuidance(handed, configured.Prompts));
        }

        /// <summary>set_camera_background: a color or a kept picture at once; a new picture is drawn first (draw_picture's job) and
        /// used when it's ready.</summary>
        private async ValueTask<ConversationToolResult> SetCameraBackgroundAsync(LiveConversationOperation? operation,
            LiveConversationConfiguration configured, ICallCamera camera, TextToolCall call, CancellationToken token)
        {
            var (arguments, problem) = CameraBackgroundTool.Parse(call.ArgumentsJson);
            if (arguments is null)
            {
                owner.tools?.Record("Martlet", CameraBackgroundTool.Name, "invalid arguments", "", true);
                return new(problem!, true);
            }
            if (arguments.Draw is { } draw)
                return StartPicture(operation, configured, draw, CameraBackgroundTool.Name,
                    (creation, later) => camera.SetBackgroundAsync(null, creation.Key, drawn: true, later));
            var result = await camera.SetBackgroundAsync(arguments.Color, arguments.Picture, drawn: false, token).ConfigureAwait(false);
            owner.tools?.Record("Martlet", CameraBackgroundTool.Name, arguments.Color is { } color ? "color " + color : "picture", "", false);
            ErrorLog.Info($"Discord call: set_camera_background chose {(arguments.Color is { } chosen ? chosen.ToString().ToLowerInvariant() : "a picture")}.");
            return new(result);
        }

        /// <summary>Runs one call of the Discord calls and camera check-in tool set, on a pool thread after the reply: the same work,
        /// arguments, gates and limits as the reply's tools of the same names. A new background picture is drawn by draw_picture's job.</summary>
        internal ValueTask<ConversationToolResult> RunDiscordCheckInToolAsync(TextToolCall call, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(call);
            switch (call.Name)
            {
                case DiscordCheckInTools.Call:
                    return owner.DiscordCaller is { CanCall: true } caller ? CallOnDiscordAsync(caller, call, token)
                        : new(new ConversationToolResult("Martlet can't call anyone on Discord now.", true));
                case DiscordCheckInTools.Background:
                    if (owner.CallCamera is not { Offered: true } camera)
                        return new(new ConversationToolResult("Martlet isn't in the owner's Discord calls, so there's no webcam background.", true));
                    if (owner.Configuration is not { } configured)
                        return new(new ConversationToolResult("The conversation isn't set up, so the background can't change now.", true));
                    return SetCameraBackgroundAsync(null, configured, camera, call, token);
                default:
                    return new(new ConversationToolResult($"This set has no tool called {call.Name}.", true));
            }
        }

        private async ValueTask<ConversationToolResult> CallOnDiscordAsync(IDiscordCaller caller, TextToolCall call, CancellationToken token)
        {
            if (DiscordCallTool.Person(call.ArgumentsJson) is not { Length: > 0 } person)
                return new("Say which friend to call (person).", true);
            var result = await caller.CallAsync(person, token).ConfigureAwait(false);
            owner.tools?.Record("Martlet", DiscordCallTool.Name, "called", "", false);
            ErrorLog.Info("Discord: call_on_discord ran.");
            return new(result);
        }

        private async ValueTask<ConversationToolResult> ReplyRemindAsync(TextToolCall call, CancellationToken token) =>
            (await RemindAsync(Reminders.ToolName, call.ArgumentsJson, null, token).ConfigureAwait(false)).Result;

        /// <summary>list_reminders: the reply's read-only reminders tool while a check-in sets and cancels them.</summary>
        private async ValueTask<ConversationToolResult> ListRemindersAsync(CancellationToken token)
        {
            var outcome = await RemindAsync(Reminders.ListToolName, Reminders.ListArgumentsJson, null, token).ConfigureAwait(false);
            return outcome.Result;
        }

        /// <summary>Runs one reminders call for the reply or, with <paramref name="checkIn"/>'s name, for a check-in: the same
        /// reminders, the same tool log line and the same log entry, so a reminder a check-in sets shows like one the reply set.
        /// A reminders call that can't run gives a failed outcome.</summary>
        internal async Task<Reminders.ToolOutcome> RemindAsync(string tool, string argumentsJson, string? checkIn, CancellationToken token)
        {
            Reminders.ToolOutcome outcome;
            if (owner.RemindersTool is not { } remind) outcome = new(new("Reminders can't be kept on this PC.", true), "unavailable", null);
            else
            {
                try { outcome = await remind(argumentsJson, token).ConfigureAwait(false); }
                catch (Exception error) when (!token.IsCancellationRequested && error is IOException or UnauthorizedAccessException or
                    ContractException or InvalidOperationException or TaskCanceledException)
                {
                    outcome = new(new("Reminders can't be changed right now. Tell the user briefly.", true), "failed", null);
                }
            }
            owner.tools?.Record(checkIn is null ? "Martlet" : "Check-in " + checkIn, tool, outcome.Outcome, "", outcome.Result.IsError);
            if (outcome.Own is not null)
                ErrorLog.Info($"Reminders: {Reminders.ToolName} {outcome.Outcome}{(checkIn is null ? "" : " by the check-in " + checkIn)}.");
            return outcome;
        }

        /// <summary>manage_memories (and find_memories, and the Memory check-in tool set's tools): finds, adds, corrects, reassigns or
        /// forgets facts. Changes are noted in the talk window like background remembering's and in the Tools log under
        /// <paramref name="tool"/>; <paramref name="by"/> names the check-in that called it (null: the reply).</summary>
        private async ValueTask<ConversationToolResult> ManageMemoriesAsync(Guid revision, string argumentsJson, Martlet.Core.Speakers.KnownVoice? speaker,
            string tool, string? by, CancellationToken token) =>
            (await RunMemoryToolAsync(revision, argumentsJson, speaker, tool, by, token).ConfigureAwait(false)).Result;

        private async Task<MemoryToolOutcome> RunMemoryToolAsync(Guid revision, string argumentsJson, Martlet.Core.Speakers.KnownVoice? speaker,
            string tool, string? by, CancellationToken token)
        {
            var roster = owner.voices?.Roster;
            var yours = owner.voices?.Yours.FirstOrDefault();
            MemoryToolOutcome outcome;
            try
            {
                outcome = await RetryStoreAsync(() => MemoryTools.RunAsync(owner.memory!, revision, argumentsJson, roster, speaker, token, yours,
                    owner.MeaningFactsAsync), token).ConfigureAwait(false);
            }
            catch (Exception error) when (!token.IsCancellationRequested && error is DesktopMemoryException or MemoryException or
                ContractException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                var why = error switch
                {
                    DesktopMemoryException app => app.Message,
                    MemoryException { Failure: MemoryFailure.Conflict or MemoryFailure.NotFound } => "The fact changed meanwhile; call find again.",
                    _ => "Memory can't be changed right now."
                };
                outcome = new(new(why + " Tell the user briefly.", true), "failed", []);
            }
            owner.tools?.Record("Martlet", tool, outcome.Outcome, "", outcome.Result.IsError);
            if (outcome.Changes.Count > 0)
            {
                ErrorLog.Info($"Memory: {tool} {outcome.Outcome}{(by is null ? "" : $" by the check-in {by}")}.");
                owner.MemoryCaptured?.Invoke(new(outcome.Changes.Select(change => change with { Person = MemoryPeople.Label(change.VoiceId, roster) }).ToArray()));
            }
            return outcome;
        }

        /// <summary>Runs one call of the Memory check-in tool set (<see cref="MemoryToolSet"/>) for the check-in <paramref name="by"/>,
        /// as its matching manage_memories action, while memory is on. "Me" is whoever spoke last.</summary>
        internal async ValueTask<ConversationToolResult> CheckInMemoryAsync(TextToolCall call, string by, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(call);
            if (MemoryToolSet.Action(call.Name) is not { } action) return new($"This set has no tool called {call.Name}.", true);
            LiveConversationConfiguration? configured;
            Martlet.Core.Speakers.KnownVoice? speaker;
            lock (owner.gate)
            {
                configured = owner.configuration;
                speaker = owner.lastAsked.Heard?.Speaker?.Voice;
            }
            if (owner.memory is null || configured?.Memory is not { Enabled: true } remembered)
                return new("Memory: off.\nMemory is off, so nothing changed.", true);
            var outcome = await RunMemoryToolAsync(remembered.ConfigurationRevision, MemoryTools.WithAction(call.ArgumentsJson, action),
                speaker, call.Name, by, token).ConfigureAwait(false);
            // The first line (shown on the card and in check-ins-status.json) is the outcome only, never a fact or a name.
            return new($"Memory: {outcome.Outcome}.\n{outcome.Result.Output}", outcome.Result.IsError);
        }

        /// <summary>search_conversations: searches the record of earlier conversations (not this one, which the model has), the
        /// replies under <paramref name="companion"/>'s name.</summary>
        private async ValueTask<ConversationToolResult> SearchConversationsAsync(TextToolCall call, Guid conversation, string companion,
            CancellationToken token)
        {
            var (result, outcome) = await owner.history!.SearchAsync(call, conversation, token, companion).ConfigureAwait(false);
            owner.tools?.Record("Martlet", PastConversations.ToolName, outcome, ConversationHistory.Preview(call.ArgumentsJson, 120), result.IsError);
            return result;
        }

        /// <summary>list_creations: what Martlet made, from this PC's copy of the shared list (read only when called).</summary>
        private ConversationToolResult ListCreations(TextToolCall call)
        {
            var directory = owner.CreationsDirectory!;
            var library = CreationStore.View(directory);
            var result = CreationTools.List(library, owner.Creations, call.ArgumentsJson, c => CreationStore.IsComplete(directory, c));
            owner.tools?.Record("Martlet", CreationTools.ListName, result.IsError ? "invalid arguments" : $"{library.Live.Count} creations", "", result.IsError);
            return result;
        }

        /// <summary>perform_creation: hands the creation to its kind's handler (a song is sung by the conversation that attached the
        /// song handler), or says clearly why it can't.</summary>
        private async ValueTask<ConversationToolResult> PerformCreationAsync(TextToolCall call, CancellationToken token)
        {
            var directory = owner.CreationsDirectory!;
            var library = CreationStore.View(directory);
            var result = await CreationTools.PerformAsync(library, owner.Creations, call.ArgumentsJson, c => CreationStore.Assets(directory, c), token)
                .ConfigureAwait(false);
            owner.tools?.Record("Martlet", CreationTools.PerformName, result.IsError ? "not performed" : "performed", "", result.IsError);
            ErrorLog.Info($"Creations: perform_creation {(result.IsError ? "didn't start" : "started")}.");
            return result;
        }

        /// <summary>think_longer: starts the background think and returns at once (never waits for it), telling the model to tell
        /// the user now unless it already did. The think continues this reply's request (what was said before the call included).</summary>
        private ValueTask<ConversationToolResult> ThinkLongerAsync(LiveConversationOperation operation, LiveConversationConfiguration configured,
            TextToolCall call) =>
            ValueTask.FromResult(StartThink(configured, operation.Sent, () => operation.Turn?.Content.Text,
                !string.IsNullOrWhiteSpace(operation.Turn?.Content.Text), call, checkIn: null));

        /// <summary>Starts a background think that continues <paramref name="sent"/> (the request of the reply that asked for it) and
        /// what that reply said (<paramref name="reply"/>). <paramref name="checkIn"/>: the check-in that started it after the
        /// exchange (null: the reply's think_longer), which gets its own words back.</summary>
        internal ConversationToolResult StartThink(LiveConversationConfiguration configured, BoundedTextInput? sent, Func<string?> reply,
            bool toldUser, TextToolCall call, string? checkIn)
        {
            const string server = "Martlet";
            var (task, reason, problem) = ThinkLonger.Parse(call.ArgumentsJson);
            if (problem is not null)
            {
                owner.tools?.Record(server, ThinkLonger.Name, "invalid arguments", "", true);
                return new(checkIn is null ? problem : BackgroundWorkTools.NotStarted("invalid arguments") + "\n" + problem, true);
            }
            var settings = configured.ThinkLonger;
            if (!settings.On) return new(checkIn is null ? ThinkLonger.TurnedOff : BackgroundWorkTools.NotStarted("Thinking longer is off"), true);
            // Where it thinks (Companion › Thinking pool, this PC's choice): every member that can run a think (a model of its own; a
            // second model in Ollama on this PC only while both fit on the graphics card) and whose computer answers now; the
            // conversation model stands in while none answers, when that is allowed.
            var live = owner.LivePool(configured);
            var plan = live.Plan;
            if (!plan.Available)
            {
                owner.tools?.Record(server, ThinkLonger.Name, "not started: unavailable", ThinkLonger.Label(task!), false);
                ErrorLog.Info($"Background thinking: a new think wasn't started ({plan.Why})");
                return new(checkIn is null ? ThinkLonger.Unavailable(plan.Why) : BackgroundWorkTools.NotStarted("Deep thinking can't run right now") + "\n" + plan.Why, true);
            }
            var thinkingModel = configured.Route(SetupRole.Llm).ModelId;
            // Members that are offline now stay in the list: the broker passes over them, and a think waiting in line goes to one as
            // soon as it answers again.
            var pool = Placing(owner.DeepPool(configured), live);
            var places = ThinkLonger.Places(pool, BackgroundDuties.Of(owner.dataDirectory), owner.PoolCan, HostRouteGpus.For, Volatile.Read(ref owner.thinkingPool));
            var runsOn = owner.LongRunsOn(places, ThinkingJobKind.ThinkLonger);
            if (runsOn.Why is { } none)
            {
                owner.tools?.Record(server, ThinkLonger.Name, "not started: unavailable", ThinkLonger.Label(task!), false);
                ErrorLog.Info($"Background thinking: a new think wasn't started ({none})");
                return new(checkIn is null ? ThinkLonger.Unavailable(none) : BackgroundWorkTools.NotStarted("Deep thinking can't run right now") + "\n" + none, true);
            }
            places = runsOn.Places;
            var thinkingRoute = configured.Routes.ThinkingRoute();
            var start = owner.jobs.Start(ThinkLonger.Kind(settings, ThinkLonger.Slots(places)) with { SmartFirst = runsOn.SmartFirst }, ThinkLonger.Label(task!), async (job, token) =>
            {
                // The place the broker picked for it: a free slot on the place sharing least with the conversation and kept free
                // for no other work, waiting in line for the first that frees up when every one is busy.
                var spot = pool.Find(job.Place!.Id)!;
                var place = spot.Settings;
                var attempts = 0;
                // The think on a place, going on from what it wrote when the live floor stopped it elsewhere (or there) before.
                BackgroundThink On(BackgroundPlace at, ThinkResume? resume)
                {
                    var here = pool.Find(at.Id) ?? spot;
                    var where = here.Settings.Separate ? here.Settings.Describe() : thinkingModel;
                    var slot = owner.ThinkSlotFor(here.Key);
                    var think = new BackgroundThink(owner.ThinkRuntime(slot),
                        left => owner.PrepareThink(configured, here.Settings, sent, reply, task!, reason, left,
                            own => Volatile.Write(ref slot.Authorization, own), resume), owner.clock)
                    {
                        AttemptFinished = terminal =>
                        {
                            attempts++;
                            owner.routing.NoteFallback("Background thinking", configured, terminal);
                            owner.NoteInput("Background thinking", terminal, reply: false);
                            if (ConversationRouting.IsFailure(terminal) && terminal.State != ConversationState.Canceled)
                            {
                                if (here.Settings.Separate) ErrorLog.Warn($"Background thinking on {where} failed ({ConversationRouting.Describe(terminal)}).");
                                else owner.routing.LogReplyFailure("Background thinking", configured, terminal);
                                // Its computer didn't answer: offline at once, so the think (and the next job) goes elsewhere.
                                if (terminal.ProviderFailure == ProviderFailureCode.Network) owner.NoteUnreachable(here.Settings, job.Id);
                            }
                        }
                    };
                    owner.thinking[job.Id] = new(think, where, here.Computer, here.Plan);
                    if (resume is not null)
                        ErrorLog.Info($"Background thinking: {job.Id} goes on on {where} (placed on {here.Computer}), " +
                            (resume.InPlace ? "continuing what it wrote." : "starting again with what it wrote as context."));
                    return think;
                }
                using var guard = CancellationTokenSource.CreateLinkedTokenSource(token);
                var watch = Task.CompletedTask;
                string? pushed = null;
                try
                {
                    if (spot.Plan.ChecksFit)
                    {
                        // A second model in the same Ollama: it starts only when both fit on the graphics card (Thinking's loaded
                        // first, so its own size counts), and stops if loading it pushed Thinking's off the card after all.
                        job.Report(BackgroundJobState.Waiting, "checking it fits beside Thinking");
                        var fit = await LocalDeepThinking.CheckAsync(thinkingModel, place.ModelId!, loadThinking: true, token).ConfigureAwait(false);
                        ErrorLog.Info($"Background thinking: {job.Id} {(fit.Fits ? "can" : "can't")} run in Ollama on this PC beside Thinking. {fit.Why}");
                        if (!fit.Fits) return BackgroundJobOutcome.Failed(fit.Why.TrimEnd('.'));
                        watch = LocalDeepThinking.WatchAsync(thinkingModel, place.ModelId!, why =>
                        {
                            Volatile.Write(ref pushed, why);
                            guard.Cancel();
                        }, guard.Token);
                    }
                    // It yields to the live conversation: stopped where the conversation needs the hardware, it goes on later.
                    return await YieldingThink.RunAsync(owner.jobs, job, On,
                        at => pool.Find(at.Id) is { } next && ThinkLonger.ContinuesInPlace(next.Settings, thinkingRoute), guard.Token,
                        (at, kept) => ErrorLog.Info($"Background thinking: {job.Id} stopped on {at.Name} for the conversation" +
                            (kept is null ? "" : $", keeping the {kept.Partial.Length} characters it wrote") + "; it goes on later."),
                        (at, began) => HeldOnHost(pool, at, began), GoesOnElsewhere("Background thinking", job.Id)).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && Volatile.Read(ref pushed) is { } pushedOut)
                {
                    ErrorLog.Warn($"Background thinking: {job.Id} stopped. {pushedOut}");
                    LocalDeepThinking.RecoverAsync(thinkingModel, place.ModelId!).Forget();
                    return BackgroundJobOutcome.Failed(pushedOut.TrimEnd('.'));
                }
                finally
                {
                    await guard.CancelAsync().ConfigureAwait(false);
                    await watch.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    owner.thinking.TryRemove(job.Id, out _);
                    ErrorLog.Info($"Background thinking: {job.Id} ended on {job.Place?.Name ?? spot.Computer} after {BackgroundJobs.Duration(job.Elapsed)} " +
                        $"({attempts} request{(attempts == 1 ? "" : "s")}, alongside the conversation" +
                        (job.Preemptions > 0 ? $"; stopped {job.Preemptions} time{(job.Preemptions == 1 ? "" : "s")} for the conversation" : "") + ").");
                }
            }, places, wait: true);
            if (start.Job is not { } started)
            {
                owner.tools?.Record(server, ThinkLonger.Name, "not started: " + start.Refusal, ThinkLonger.Label(task!), false);
                ErrorLog.Info($"Background thinking: a new think wasn't started ({start.Refusal}: {start.Message})");
                return new(checkIn is null ? ThinkLonger.Refused(start) : BackgroundWorkTools.NotStarted(start.Refusal ?? "unavailable") +
                    (start.Message is { } refusal ? "\n" + refusal : ""), true);
            }
            var by = checkIn is null ? (toldUser ? "." : " The reply hadn't told you yet, so it was asked to.") : $" (started by the check-in {checkIn}).";
            var slots = ThinkLonger.Slots(places);
            var busy = owner.jobs.Places.Leases.Count(lease => places.Any(p => p.Id == lease.Place.Id));
            var terms = $"thinking steps on, {settings.HowHard} effort, no time limit, " +
                $"{owner.jobs.StartedWithinHour(ThinkLonger.KindName)} this hour (no hourly limit)";
            if (started.Place is not { } seat)
            {
                owner.tools?.Record(server, ThinkLonger.Name, "queued " + started.Id, ThinkLonger.Label(task!), false);
                ErrorLog.Info($"Background thinking: {started.Id} waits in line ({owner.jobs.Places.Position(started.Id)} in line) for one of " +
                    $"{places.Count} place{(places.Count == 1 ? "" : "s")} ({ThinkLonger.AtOnce(places)} at once; " +
                    (start.ForConversation ? "the live floor keeps them free for the conversation" : $"busy: {start.Queued}") + $"; {terms})" + by);
                return new(checkIn is null ? ThinkLonger.Started(started, toldUser, start.Queued, start.ForConversation)
                    : BackgroundWorkTools.Started(started, start.Queued, start.ForConversation));
            }
            owner.tools?.Record(server, ThinkLonger.Name, "started " + started.Id, ThinkLonger.Label(task!), false);
            var chosen = pool.Find(seat.Id)!;
            ErrorLog.Info($"Background thinking: started {started.Id} on {(chosen.Settings.Separate ? chosen.Settings.Describe() : thinkingModel)} " +
                $"(placed on {chosen.Computer}{(seat.Duties.Count > 0 ? $", also kept for {string.Join(" and ", seat.Duties)}" : "")}, " +
                $"{busy} of {slots} slot{(slots == 1 ? "" : "s")} on {places.Count} place{(places.Count == 1 ? "" : "s")} busy; {terms}; " +
                $"in parallel with the conversation: {chosen.Plan.Why})" + by);
            return new(checkIn is null ? ThinkLonger.Started(started, toldUser) : BackgroundWorkTools.Started(started));
        }

        /// <summary>cancel_thinking: stops the running think; nothing about it is brought up later (Martlet knows).</summary>
        private ConversationToolResult CancelThinking(TextToolCall call)
        {
            var stopped = owner.jobs.Cancel(ThinkLonger.CancelId(call.ArgumentsJson), BackgroundJob.CanceledByMartlet, ThinkLonger.KindName);
            owner.tools?.Record("Martlet", ThinkLonger.CancelName, stopped is null ? "nothing to stop" : "stopped " + stopped.Id, "", false);
            if (stopped is not null) ErrorLog.Info($"Background thinking: Martlet stopped {stopped.Id}.");
            return new(ThinkLonger.Canceled(stopped));
        }

        /// <summary>The talk window's Cancel on a background job: it stops, and the next thing the user says tells Martlet so.</summary>
        internal bool CancelJob(string id)
        {
            var stopped = owner.jobs.Cancel(id, BackgroundJob.CanceledByYou);
            if (stopped is not null) ErrorLog.Info($"Background work: you canceled {stopped.Id}.");
            return stopped is not null;
        }

        /// <summary>The conversation ended (the talk window closed): background work stops and nothing more is brought up, and a song
        /// stops at once.</summary>
        internal void EndBackgroundWork()
        {
            if (owner.jobs.Active.Count > 0) ErrorLog.Info("Background work: the conversation ended, so its background work stopped.");
            owner.jobs.CancelAll();
            owner.singing?.Stop(SongStopCause.Button, musical: false, reason: "closing the conversation");
            owner.floor.Clear("the conversation ended");
        }

        // ---------- singing (sing_song, play_song, stop_singing) ----------

        /// <summary>sing_song: starts the song job and returns at once, telling the model to tell the user now unless it already did.
        /// Without lyrics, the job first writes them (and the style, tempo and key) with a background think that continues this
        /// reply's request like think_longer's (Thinking steps on, where Deep thinking thinks, waiting for quiet moments when it shares
        /// the conversation's hardware); then the song maker makes the song in the chosen voice, and the library keeps it.
        /// <paramref name="afterReply"/>: a check-in calls it after the reply <paramref name="operation"/> (null: none yet), which
        /// already told the user.</summary>
        private ConversationToolResult SingSong(LiveConversationOperation? operation, LiveConversationConfiguration configured, TextToolCall call,
            bool afterReply = false)
        {
            const string server = "Martlet";
            var (arguments, problem) = SongTools.ParseSing(call.ArgumentsJson);
            if (arguments is null)
            {
                owner.tools?.Record(server, SongTools.SingName, "invalid arguments", "", true);
                return new(problem!, true);
            }
            var label = SongTools.Label(arguments.About);
            var (setup, unavailable) = owner.singing?.Source?.Current() ?? (null, "singing isn't set up.");
            if (setup is null || owner.singing?.DataDirectory is null || owner.CreationsDirectory is not { } library)
            {
                owner.tools?.Record(server, SongTools.SingName, "not started: singing unavailable", label, false);
                ErrorLog.Info($"Singing: a song wasn't started ({unavailable}).");
                return new(SongTools.Unavailable(unavailable ?? "singing isn't set up."), true);
            }
            var toldUser = afterReply || !string.IsNullOrWhiteSpace(operation?.Turn?.Content.Text);
            var sent = operation?.Sent;
            Func<string, CancellationToken, Task<LyricsWriter>>? writer = null;
            var where = "";
            if (arguments.Lyrics is null)
            {
                // The lyrics are written where Deep thinking thinks, alongside the conversation (never on Thinking's own model, so
                // replies never wait): on the free place that shares least with the conversation, else the least busy one, held while
                // it writes. Without a Deep thinking place, the reply writes them itself.
                var live = owner.LivePool(configured);
                var plan = live.Plan;
                if (!plan.Available)
                {
                    owner.tools?.Record(server, SongTools.SingName, "not started: lyrics needed", label, false);
                    return new(SongTools.WriteLyricsYourself(plan.Why), true);
                }
                var thinkingModel = configured.Route(SetupRole.Llm).ModelId;
                // Members that are offline now stay in the list: the broker passes over them until they answer again.
                var pool = Placing(owner.DeepPool(configured), live);
                var places = ThinkLonger.Places(pool, BackgroundDuties.Of(owner.dataDirectory), owner.PoolCan, choices: Volatile.Read(ref owner.thinkingPool));
                // A song's lyrics are a think: they run where thinking longer's Runs on says.
                var runsOn = owner.LongRunsOn(places, ThinkingJobKind.ThinkLonger);
                if (runsOn.Why is { } none)
                {
                    owner.tools?.Record(server, SongTools.SingName, "not started: lyrics needed", label, false);
                    return new(SongTools.WriteLyricsYourself(none), true);
                }
                places = runsOn.Places;
                where = places.Count == 1
                    ? pool.Find(places[0].Id)?.Settings is { Separate: true } only ? only.Describe() : thinkingModel
                    : $"whichever of {places.Count} Thinking pool members is free";
                var task = SongTools.WritingTask(configured.Prompts, arguments);
                writer = async (holder, wait) =>
                {
                    var runtime = SongRuntime();
                    // The broker's choice, as for a think: waits in line while every place is busy.
                    var lease = await owner.jobs.Places.AcquireAsync(places, holder, wait,
                        ThinkingDemand.For(ThinkingJobKind.ThinkLonger, places) with { SmartFirst = runsOn.SmartFirst }).ConfigureAwait(false);
                    var spot = pool.Find(lease.Place.Id)!;
                    var place = spot.Settings;
                    var at = place.Separate ? place.Describe() : thinkingModel;
                    ErrorLog.Info($"Singing: {holder} writes its lyrics on {at} (placed on {spot.Computer}, " +
                        $"{owner.jobs.Places.Load(lease.Place.Id)} on it now).");
                    var think = new BackgroundThink(runtime,
                        left => owner.PrepareThink(configured, place, sent, () => operation?.Turn?.Content.Text, task, null, left,
                            own => Volatile.Write(ref songAuthorization, own)), owner.clock)
                    {
                        Doing = "Writing the lyrics",
                        AttemptFinished = terminal =>
                        {
                            owner.routing.NoteFallback("Song lyrics", configured, terminal);
                            owner.NoteInput("Song lyrics", terminal, reply: false);
                            if (ConversationRouting.IsFailure(terminal) && terminal.State != ConversationState.Canceled)
                                ErrorLog.Warn($"Singing: writing the lyrics on {at} failed ({ConversationRouting.Describe(terminal)}).");
                        }
                    };
                    return new(think, spot.Plan.ChecksFit ? (thinkingModel, place.ModelId!) : null, lease);
                };
            }
            // Writing the lyrics may take the song's whole time limit (a think has none of its own).
            var writing = SongTools.Kind.TimeLimit!.Value;
            var author = new CreationAuthor
            {
                Device = HostSetupCommands.SuggestedDeviceId(), Computer = Environment.MachineName, Voice = setup.VoiceId,
                Persona = configured.Persona?.Name is { Length: > 0 } persona ? persona : null
            };
            var start = owner.jobs.Start(SongTools.Kind, label, (job, token) =>
                MakeSongAsync(job, arguments, setup, library, author, writer, writing, token));
            if (start.Job is not { } started)
            {
                owner.tools?.Record(server, SongTools.SingName, "not started: " + start.Refusal, label, false);
                ErrorLog.Info($"Singing: a new song wasn't started ({start.Refusal}).");
                return new(SongTools.Refused(start), true);
            }
            owner.tools?.Record(server, SongTools.SingName, "started " + started.Id, label, false);
            ErrorLog.Info($"Singing: started {started.Id} ({arguments.Seconds} s, {(writer is null ? "lyrics given" : $"lyrics written on {where}")}, " +
                $"made on {setup.Where}; " +
                $"{owner.jobs.StartedWithinHour(SongTools.KindName)} of {SongTools.PerHour} this hour)" +
                (toldUser ? "." : " The reply hadn't told you yet, so it was asked to."));
            return new(SongTools.Started(started, toldUser, writer is not null));
        }

        // The song job: the singing computer is checked, the lyrics written (when none were given), the song made, its mouth timed
        // to its vocals and the song kept as a creation (shared with every paired Martlet computer).
        private async Task<BackgroundJobOutcome> MakeSongAsync(BackgroundJob job, SingArguments arguments, SongSetup setup, string library,
            CreationAuthor author, Func<string, CancellationToken, Task<LyricsWriter>>? writerFor, TimeSpan writing, CancellationToken token)
        {
            job.Report(BackgroundJobState.Running, "Checking the singing computer");
            var availability = await setup.Maker.GetAvailabilityAsync(token).ConfigureAwait(false);
            if (!availability.Available) return BackgroundJobOutcome.Failed(availability.Reason ?? "singing isn't available right now");
            WrittenSong? written;
            string? problem;
            if (writerFor is not null)
            {
                LyricsWriter seated;
                using (var patience = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    patience.CancelAfter(writing);
                    job.Report(BackgroundJobState.Waiting, "waiting for a free computer to write the lyrics");
                    try { seated = await writerFor(job.Id, patience.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        return BackgroundJobOutcome.Failed($"no computer came free to write the lyrics within {BackgroundJobs.Duration(writing)}");
                    }
                }
                job.Report(BackgroundJobState.Running);
                var (writer, beside, place) = seated;
                using var held = place;
                BackgroundJobOutcome lyrics;
                using (var limit = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    limit.CancelAfter(writing);
                    var watch = Task.CompletedTask;
                    string? pushed = null;
                    try
                    {
                        if (beside is { } models)
                        {
                            // A second model in the same Ollama writes only while both fit on the graphics card, as a think does.
                            job.Report(BackgroundJobState.Waiting, "checking it fits beside Thinking");
                            var fit = await LocalDeepThinking.CheckAsync(models.Thinking, models.Deep, loadThinking: true, limit.Token).ConfigureAwait(false);
                            if (!fit.Fits) return BackgroundJobOutcome.Failed(fit.Why.TrimEnd('.'));
                            watch = LocalDeepThinking.WatchAsync(models.Thinking, models.Deep, why =>
                            {
                                Volatile.Write(ref pushed, why);
                                limit.Cancel();
                            }, limit.Token);
                        }
                        lyrics = await writer.RunAsync(job, limit.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        if (Volatile.Read(ref pushed) is { } pushedOut && beside is { } models)
                        {
                            LocalDeepThinking.RecoverAsync(models.Thinking, models.Deep).Forget();
                            return BackgroundJobOutcome.Failed(pushedOut.TrimEnd('.'));
                        }
                        return BackgroundJobOutcome.Failed($"writing the lyrics took longer than {BackgroundJobs.Duration(writing)}");
                    }
                    finally
                    {
                        await limit.CancelAsync().ConfigureAwait(false);
                        await watch.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    }
                }
                if (lyrics.Result is not { } text) return BackgroundJobOutcome.Failed(lyrics.Problem ?? "the lyrics couldn't be written");
                (written, problem) = SongTools.ParseWritten(text, arguments);
            }
            else (written, problem) = SongTools.ParseWritten("LYRICS:\n" + arguments.Lyrics, arguments);
            if (written is null) return BackgroundJobOutcome.Failed(problem ?? "the lyrics didn't come out right");
            // The singing computer is busy with this song until it is done (after the lyrics, which may be written there): no
            // background think is placed there meanwhile. The hold moves to the computer the singing pool gives the song to.
            using var singer = new SingerHold(owner.jobs.Places, job.Id, BackgroundDuties.Singer(owner.dataDirectory));
            job.Report(BackgroundJobState.Running, "Writing the music");
            var request = new SongRequest
            {
                Lyrics = written.Lyrics, Style = written.Style, VoiceId = setup.VoiceId, DurationSeconds = arguments.Seconds,
                Bpm = written.Bpm, Key = written.Key
            };
            SongResult result;
            try { result = await setup.Maker.GenerateAsync(request, new SongJobProgress(job, singer), token).ConfigureAwait(false); }
            catch (SongException error) { return BackgroundJobOutcome.Failed(SongProblem(error)); }
            job.Report(BackgroundJobState.Running, "Timing the mouth to the singing");
            var (mouth, words, estimated, timing) = await owner.singing!.MouthAsync(result, token).ConfigureAwait(false);
            job.Report(BackgroundJobState.Running, "Saving the song");
            Creation creation;
            try
            {
                creation = await CreationStore.AddAsync(library, SongCreations.Draft(result, written.Title, arguments.About, written.Lyrics,
                    written.Style, words, estimated, mouth, author), CreationRegistry.Shared, owner.clock.GetUtcNow(), token).ConfigureAwait(false);
            }
            catch (Exception error) when (CreationStore.IsFailure(error))
            {
                ErrorLog.Warn($"Singing: {job.Id} couldn't keep its song ({error.Message}).");
                return BackgroundJobOutcome.Failed("there was no room to keep the song on this PC");
            }
            var (song, _, _, unreadable) = await SongCreations.LoadAsync(creation, CreationStore.Assets(library, creation), token).ConfigureAwait(false);
            if (song is null) return BackgroundJobOutcome.Failed(unreadable ?? "the song couldn't be read back");
            owner.singing.Made(song);
            ErrorLog.Info($"Singing: {song.Id} mouth track from {mouth.Note} ({mouth.Frames} frames): mouth opens {timing.MedianOffsetMs:+0;-0;0} ms " +
                $"from the vocal onsets (median; mean {timing.MeanAbsoluteOffsetMs:0} ms, 90% within {timing.P90AbsoluteOffsetMs:0} ms; " +
                $"{timing.Matched} of {timing.Onsets} onsets; words {(estimated ? "spread over each line's singing" : "timed by " + result.WordTimingSource)}).");
            ErrorLog.Info($"Singing: {job.Id} made {song.Id} ({SongClock.Of(TimeSpan.FromSeconds(song.DurationSeconds))}, " +
                $"{song.Lines.Count} lines, {song.Generator} + {song.Converter}{(song.Fixture ? ", FIXTURE - NOT AI" : "")}; " +
                $"{creation.Bytes / 1024} KiB kept as a creation) after " +
                $"{BackgroundJobs.Duration(job.Elapsed)}: " + string.Join(", ", result.StageTimings.Select(stage => $"{stage.Stage} {stage.Duration.TotalSeconds:0.0} s")) + ".");
            return BackgroundJobOutcome.Done(SongTools.Ready(song));
        }

        private static string SongProblem(SongException error) => error.Code switch
        {
            SongErrorCodes.Unavailable => "the singing computer isn't reachable or ready",
            SongErrorCodes.Busy => "the singing computer is busy with other songs",
            SongErrorCodes.VoiceMissing => "the voice isn't on the singing computer yet",
            SongErrorCodes.VoiceMatchUnavailable => "the chosen voice match isn't set up on the singing computer",
            SongErrorCodes.RequestInvalid => "the song request wasn't valid (" + error.Message.TrimEnd('.') + ")",
            SongErrorCodes.TimedOut => "making it took too long",
            _ => "making the music failed on the singing computer"
        };

        // The song maker's stages, on the job's chip ("Writing the music", "Matching the singing to the voice"). The report that
        // names the computer the singing pool gave the song to moves the singer's hold there.
        private sealed class SongJobProgress(BackgroundJob job, SingerHold singer) : IProgress<SongProgress>
        {
            public void Report(SongProgress value)
            {
                if (value.Host is { } host) singer.Move(host);
                if (value.Stage != SongStage.Completed) job.Report(BackgroundJobState.Running, value.Describe());
            }
        }

        // Holds the computer that makes a song from background thinks (BackgroundPlaces.Hold): Companion › Singing's computer
        // first, then the one the singing pool gives the song to. Progress reports come one at a time from the song's own flow.
        private sealed class SingerHold(BackgroundPlaces places, string holder, BackgroundPlace? first) : IDisposable
        {
            private BackgroundPlaceLease? lease = first is null ? null : places.Hold(first, holder);
            private string? host = first?.Id;
            private bool done;

            public void Move(string to)
            {
                if (done || BackgroundDuties.SingerOn(to) is not { } place || place.Id == host) return;
                var old = lease;
                lease = places.Hold(place, holder);
                host = place.Id;
                old?.Dispose();
            }

            public void Dispose()
            {
                done = true;
                lease?.Dispose();
                lease = null;
            }
        }

        /// <summary>play_song: plays a finished song (a song creation, which may have been made on another computer) through
        /// Martlet's voice output from where the model chose, with a musical lead-in anywhere but the top; returns at once.</summary>
        private async ValueTask<ConversationToolResult> PlaySongAsync(LiveConversationConfiguration configured, TextToolCall call,
            CancellationToken token)
        {
            const string server = "Martlet";
            var (id, from, problem) = SongTools.ParsePlay(call.ArgumentsJson);
            if (problem is not null)
            {
                owner.tools?.Record(server, SongTools.PlayName, "invalid arguments", "", true);
                return new(problem, true);
            }
            var directory = owner.singing?.DataDirectory is null ? null : owner.CreationsDirectory;
            if (directory is null || SongCreations.Find(directory, id) is not { } creation)
            {
                owner.tools?.Record(server, SongTools.PlayName, "no such song", "", true);
                var kept = directory is null ? [] : SongCreations.List(directory).Take(5).Select(s => $"{s.Key} (\"{s.Title}\")").ToArray();
                return new(kept.Length == 0 ? $"There's no song {id}, and no finished songs yet."
                    : $"There's no song {id}. The newest songs are {string.Join(", ", kept)}.", true);
            }
            var result = await PerformSongAsync(configured, creation, CreationStore.Assets(directory, creation), from, token).ConfigureAwait(false);
            owner.tools?.Record(server, SongTools.PlayName, result.IsError ? "not played" : "playing " + creation.Key, creation.Key, result.IsError);
            return new(result.Text, result.IsError);
        }

        // Sings a song creation from where the model chose (play_song, or perform_creation's from).
        private async ValueTask<CreationActionResult> PerformSongAsync(LiveConversationConfiguration? configured, Creation creation,
            ICreationAssets assets, string? from, CancellationToken token)
        {
            if (owner.singing is null || configured is null) return new("Martlet can't sing in this conversation.", true);
            var (song, audio, mouth, missing) = await SongCreations.LoadAsync(creation, assets, token).ConfigureAwait(false);
            if (song is null || audio is null) return new($"{missing} Say you'll sing it in a moment.", true);
            var speaking = owner.ReplySpeaking();
            var (player, trouble) = owner.singing.Play(song, audio, mouth, from, configured.SpeechOutput(), owner.ReplySpeaking);
            return player is null ? new(trouble ?? "It couldn't play.", true) : new(SongTools.Playing(song, player.Plan, speaking));
        }

        /// <summary>The song kind's handler for perform_creation: options {"from": ...} as play_song's.</summary>
        internal ValueTask<CreationActionResult> SingCreationAsync(CreationAction action, CancellationToken token)
        {
            var from = action.Options.ValueKind == System.Text.Json.JsonValueKind.Object && action.Options.TryGetProperty("from", out var value) &&
                value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : null;
            return PerformSongAsync(owner.Configuration, action.Creation, action.Assets, from, token);
        }

        /// <summary>stop_singing: the song ends musically; the result says where and why (Martlet knows, so no note follows).</summary>
        private ConversationToolResult StopSinging(TextToolCall call)
        {
            var record = owner.singing?.Stop(SongStopCause.Martlet, musical: true, reason: SongTools.ParseStop(call.ArgumentsJson));
            owner.tools?.Record("Martlet", SongTools.StopName, record is null ? "nothing to stop" : "stopped " + record.SongId, "", false);
            return new(SongTools.Stopped(record));
        }

        /// <summary>Whether a reply sings through song tags (<see cref="SongTags"/>) in place of the song tools: singing is set up,
        /// the Singing without tools prompt isn't empty and the Thinking route can't take tools (a host's gateway, or a model that
        /// rejected them). It depends on settings alone, so every request starts the same.</summary>
        internal bool SingsWithTags(LiveConversationConfiguration configured) =>
            owner.singing is { Offered: true } && !(configured.SupportsTools && owner.tools is not null && !owner.tools.IsUnsupported(configured.ToolModelKey())) &&
            SongTags.Instructions(configured.Prompts) is not null;

        /// <summary>Does what the song tags of a finished reply ask for, in order, as the song tools would: [sing: ...] starts the song
        /// (the reply already told the user), [play_song: ...] plays one and [stop_singing] stops it. A reply that stayed quiet
        /// (<paramref name="passed"/>) may only stop the song. What couldn't be done, Martlet brings up on its own.</summary>
        internal void SingFromTags(LiveConversationOperation operation, LiveConversationConfiguration configured, IReadOnlyList<SongTagCall> calls,
            bool passed)
        {
            foreach (var tag in calls)
            {
                if (passed && tag.Name != SongTools.StopName) continue;
                var call = new TextToolCall("tag-" + Guid.NewGuid().ToString("N")[..12], tag.Name, tag.ArgumentsJson);
                ErrorLog.Info($"Singing: the reply wrote a {tag.Name} tag (it sings through tags; the Thinking route takes no tools).");
                switch (tag.Name)
                {
                    case SongTools.SingName:
                        TellSongProblem(tag.Name, SingSong(operation, configured, call, afterReply: true));
                        break;
                    case SongTools.PlayName:
                        PlayFromTagAsync(configured, call).Forget();
                        break;
                    default:
                        StopSinging(call);
                        break;
                }
            }
        }

        private async Task PlayFromTagAsync(LiveConversationConfiguration configured, TextToolCall call) =>
            TellSongProblem(call.Name, await PlaySongAsync(configured, call, CancellationToken.None).ConfigureAwait(false));

        // A song tag that couldn't be done: Martlet tells the user why on its own (the reply can't hear a tool's answer).
        private void TellSongProblem(string name, ConversationToolResult result)
        {
            if (!result.IsError) return;
            var text = CreationsCheckIn.TellUser(result) ? result.Output
                : name == SongTools.SingName ? SongTools.Unavailable("the words can't be written in the background here.")
                : result.Output + " Tell the user in a few words, in character.";
            var told = owner.BringUp("Singing", text) is not null;
            ErrorLog.Info($"Singing: the {name} tag wasn't done" + (told ? "; Martlet tells you why on its own." : "."));
        }

        // ---------- pictures (draw_picture) ----------

        /// <summary>draw_picture: starts the picture job where Companion › Picture making says and returns at once, telling the model to
        /// tell the user now unless it already did. The picture is kept as a creation and shown in the talk window when it's done.</summary>
        private ConversationToolResult DrawPicture(LiveConversationOperation operation, LiveConversationConfiguration configured, TextToolCall call)
        {
            const string server = "Martlet";
            var (arguments, problem) = PictureTools.Parse(call.ArgumentsJson);
            if (arguments is null)
            {
                owner.tools?.Record(server, PictureTools.DrawName, "invalid arguments", "", true);
                return new(problem!, true);
            }
            return StartPicture(operation, configured, arguments, PictureTools.DrawName, then: null);
        }

        /// <summary>Starts a picture job for <paramref name="tool"/>; <paramref name="then"/> runs once the picture is kept (and its
        /// words are added to the note the conversation gets). <paramref name="operation"/>: the reply that asked (null: a check-in
        /// after the reply, which already told the user).</summary>
        private ConversationToolResult StartPicture(LiveConversationOperation? operation, LiveConversationConfiguration configured,
            DrawArguments arguments, string tool, Func<Creation, CancellationToken, Task<string>>? then)
        {
            const string server = "Martlet";
            var maker = owner.dataDirectory is null ? null
                : PictureClient.For(owner.dataDirectory, configured.Profile, configured.Routes.ThinkingRoute());
            if (maker is null)
            {
                owner.tools?.Record(server, tool, "not started: pictures off", arguments.About, false);
                return new(PictureTools.Unavailable("pictures aren't set up (Companion › Picture making)"), true);
            }
            var toldUser = operation is null || !string.IsNullOrWhiteSpace(operation.Turn?.Content.Text);
            var author = new CreationAuthor
            {
                Device = HostSetupCommands.SuggestedDeviceId(), Computer = Environment.MachineName,
                Persona = configured.Persona?.Name is { Length: > 0 } persona ? persona : null
            };
            var start = owner.jobs.Start(PictureTools.Kind, arguments.About, (job, token) => MakePictureAsync(job, arguments, maker, author, then, token));
            if (start.Job is not { } started)
            {
                (maker as IDisposable)?.Dispose();
                owner.tools?.Record(server, tool, "not started: " + start.Refusal, arguments.About, false);
                ErrorLog.Info($"Pictures: a new picture wasn't started ({start.Refusal}).");
                return new(PictureTools.Refused(start), true);
            }
            owner.tools?.Record(server, tool, "started " + started.Id, arguments.About, false);
            ErrorLog.Info($"Pictures: started {started.Id} ({PictureShapes.Name(arguments.Shape)}, {arguments.Description.Length} characters) on {maker.Where}; " +
                $"{owner.jobs.StartedWithinHour(PictureTools.KindName)} of {PictureTools.PerHour} this hour.");
            return new(PictureTools.Started(started, toldUser, maker.Where) + (then is null ? "" : CameraBackgroundTool.WillUse));
        }

        // The picture job: the place is checked, the picture drawn, kept as a creation (shared with every paired Martlet computer)
        // and shown in the talk window.
        private async Task<BackgroundJobOutcome> MakePictureAsync(BackgroundJob job, DrawArguments arguments, IPictureMaker maker,
            CreationAuthor author, Func<Creation, CancellationToken, Task<string>>? then, CancellationToken token)
        {
            // The pictures computer is busy with this picture until it is drawn: no background think is placed there meanwhile. With
            // more than one place in the Picture making list, the hold moves to the one that takes the picture (none for an address or a
            // cloud provider).
            var planned = BackgroundDuties.Painter(owner.dataDirectory);
            BackgroundPlaceLease? painter = planned is { } computer ? owner.jobs.Places.Hold(computer, job.Id) : null;
            void Moved(PictureMakingPoolMember member)
            {
                if (member.HostId is not { } host)
                {
                    Interlocked.Exchange(ref painter, null)?.Dispose();
                    return;
                }
                var place = BackgroundDuties.PainterOn(host);
                if (place.Id == Volatile.Read(ref painter)?.Place.Id) return;
                Interlocked.Exchange(ref painter, owner.jobs.Places.Hold(place, job.Id))?.Dispose();
            }
            if (maker is PictureMakingPool pool) pool.Placed += Moved;
            try
            {
                job.Report(BackgroundJobState.Running, "Checking where it's drawn");
                var availability = await maker.GetAvailabilityAsync(token).ConfigureAwait(false);
                if (!availability.Available) return BackgroundJobOutcome.Failed((availability.Reason ?? "pictures aren't available right now").TrimEnd('.'));
                var request = PictureTools.Request(arguments);
                PictureResult result;
                try
                {
                    result = await maker.GenerateAsync(request, new Progress<PictureProgress>(p => job.Report(BackgroundJobState.Running, p.Describe())), token)
                        .ConfigureAwait(false);
                }
                catch (PictureException error)
                {
                    ErrorLog.Warn($"Pictures: {job.Id} failed on {maker.Where} ({error.Code}: {error.Message}).");
                    return BackgroundJobOutcome.Failed(PictureClient.Problem(error));
                }
                job.Report(BackgroundJobState.Running, "Keeping the picture");
                Creation creation;
                try
                {
                    creation = await CreationStore.AddAsync(owner.CreationsDirectory!, PictureCreations.Draft(result, arguments.Title, arguments.About, request, author),
                        CreationRegistry.Shared, owner.clock.GetUtcNow(), token).ConfigureAwait(false);
                }
                catch (Exception error) when (CreationStore.IsFailure(error))
                {
                    ErrorLog.Warn($"Pictures: {job.Id} couldn't keep its picture ({error.Message}).");
                    return BackgroundJobOutcome.Failed("there was no room to keep the picture on this PC");
                }
                ErrorLog.Info($"Pictures: {job.Id} drew {creation.Key} on {result.Where} ({result.Width}x{result.Height} {result.MediaType}, " +
                    $"{result.Image.Length / 1024} KiB, {result.Model}{(result.Fixture ? ", FIXTURE - NOT AI" : "")}) in {result.Took.TotalSeconds:0.0} s.");
                owner.PictureShown?.Invoke(new(creation.Key, creation.Title ?? arguments.Title, result.Image, result.Fixture));
                var used = then is null ? "" : " " + await then(creation, token).ConfigureAwait(false);
                return BackgroundJobOutcome.Done(PictureTools.Ready(creation.Key, creation.Title ?? arguments.Title, result) + used);
            }
            finally
            {
                if (maker is PictureMakingPool placed) placed.Placed -= Moved;
                Interlocked.Exchange(ref painter, null)?.Dispose();
                PictureClient.FreeLater(maker);
                (maker as IDisposable)?.Dispose();
            }
        }

        /// <summary>The picture kind's handler for perform_creation: shows it in the talk window again.</summary>
        internal async ValueTask<CreationActionResult> ShowPictureCreationAsync(CreationAction action, CancellationToken token)
        {
            var (image, _, problem) = await PictureCreations.LoadAsync(action.Creation, action.Assets, token).ConfigureAwait(false);
            if (image is null) return new($"{problem} Say you'll show it in a moment.", true);
            var title = action.Creation.Title ?? "a picture";
            owner.PictureShown?.Invoke(new(action.Creation.Key, title, image, PictureCreations.Metadata(action.Creation)?.Fixture == true));
            return new(PictureTools.Shown(title));
        }

        // ---------- body motions (body_motion) ----------

        /// <summary>body_motion: a tag the character has plays at once; a new motion is made in the background on the motion
        /// computer (one at a time), kept, and played when it is ready. It returns at once, so speech never waits for a motion.</summary>
        private async ValueTask<ConversationToolResult> BodyMotionAsync(TextToolCall call, CancellationToken token)
        {
            const string server = "Martlet";
            var (arguments, problem) = MotionTools.Parse(call.ArgumentsJson);
            if (arguments is null)
            {
                owner.tools?.Record(server, MotionTools.Name, "invalid arguments", "", true);
                return new(problem!, true);
            }
            if (owner.Motions is not { } motions)
            {
                owner.tools?.Record(server, MotionTools.Name, "not started: motions off", arguments.Name, false);
                return new(MotionTools.Unavailable("motions aren't set up"), true);
            }
            if (await motions.Player.PlayTagAsync(arguments.Name, "body_motion", token).ConfigureAwait(false) is { } played)
            {
                owner.tools?.Record(server, MotionTools.Name, "played " + arguments.Name, arguments.Name, false);
                return new(played);
            }
            if (arguments.Motion is null)
            {
                owner.tools?.Record(server, MotionTools.Name, "needs a motion", arguments.Name, true);
                return new(MotionTools.NeedsMotion(arguments.Name), true);
            }
            return StartMotion(motions, arguments);
        }

        /// <summary>Starts a motion job: made on the motion computer, kept as a creation and played on the character when it is
        /// ready, if the same conversation goes on. Quiet: the conversation hears of it with the user's next message.</summary>
        private ConversationToolResult StartMotion(CharacterMotions motions, MotionArguments arguments)
        {
            const string server = "Martlet";
            if (motions.Studio() is not { } studio)
            {
                owner.tools?.Record(server, MotionTools.Name, "not started: motions off", arguments.Name, false);
                return new(MotionTools.Unavailable("motions aren't set up"), true);
            }
            var conversation = owner.ConversationId;
            var start = owner.jobs.Start(MotionTools.Kind, arguments.Name, (job, token) => MakeMotionAsync(job, motions, arguments, studio, conversation, token));
            if (start.Job is not { } started)
            {
                studio.Dispose();
                owner.tools?.Record(server, MotionTools.Name, "not started: " + start.Refusal, arguments.Name, false);
                ErrorLog.Info($"Motions: a new motion wasn't started ({start.Refusal}).");
                return new(MotionTools.Refused(start), true);
            }
            owner.tools?.Record(server, MotionTools.Name, "started " + started.Id, arguments.Name, false);
            ErrorLog.Info($"Motions: started {started.Id} ({arguments.Name}, {MotionCreations.Number(arguments.Seconds)} s, {arguments.Motion!.Length} characters) " +
                $"on {studio.Where}; {owner.jobs.StartedWithinHour(MotionTools.KindName)} of {MotionTools.PerHour} this hour.");
            return new(MotionTools.Started(started, studio.Where, arguments.Name));
        }

        // The motion job: holds the motion computer away from background thinks, makes and keeps the motion, plays it if the same
        // conversation goes on, and frees the motion computer's GPU a while after the last motion.
        private async Task<BackgroundJobOutcome> MakeMotionAsync(BackgroundJob job, CharacterMotions motions, MotionArguments arguments,
            IMotionStudio studio, Guid conversation, CancellationToken token)
        {
            using var hold = studio.HostId is { } host ? owner.jobs.Places.Hold(BackgroundDuties.AnimatorOn(host), job.Id) : null;
            try
            {
                var (creation, made) = await motions.MakeAndKeepAsync(arguments, studio, step => job.Report(BackgroundJobState.Running, step), token)
                    .ConfigureAwait(false);
                var notPlayed = owner.ConversationId != conversation ? "the conversation it was for ended"
                    : await motions.Player.PlayKeptAsync(creation.Key, "body_motion", token).ConfigureAwait(false);
                ErrorLog.Info($"Motions: {job.Id} made {creation.Key} ({arguments.Name}, {made.Clip.Length / 1024} KiB, {MotionCreations.Number(made.Seconds)} s, " +
                    $"{made.Model}{(made.Fixture ? ", FIXTURE - NOT AI" : "")}) on {made.Where} in {made.Took.TotalSeconds:0.0} s; " +
                    (notPlayed is null ? "played it." : $"didn't play it ({notPlayed})."));
                return BackgroundJobOutcome.Done(MotionTools.Ready(creation.Key, arguments.Name, made, notPlayed));
            }
            catch (MotionStudioException error)
            {
                ErrorLog.Warn($"Motions: {job.Id} failed on {studio.Where} ({error.Message}).");
                return BackgroundJobOutcome.Failed(error.Message);
            }
            finally
            {
                studio.FreeLater();
                studio.Dispose();
            }
        }

        // ---------- after the reply: the Songs, pictures and creations check-in tool set ----------

        /// <summary>A check-in's call of a Songs, pictures and creations tool (<see cref="CreationsCheckIn"/>), after the reply: the
        /// reply's own tool does the work with the same arguments, limits and gates, so its job and the note when it's done reach
        /// the conversation the same way. A start that was refused or can't happen now is brought up by Martlet on its own, as the
        /// reply would have said it. The first line of the answer says only the tool and what came of it.</summary>
        internal async ValueTask<ConversationToolResult> CreationsCheckInAsync(TextToolCall call, CheckInToolContext context, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(call);
            ArgumentNullException.ThrowIfNull(context);
            if (owner.Configuration is not { } configured || owner.dataDirectory is null)
                return new($"{call.Name}: not done.\nMartlet isn't talking now, so it can't do that.", true);
            var sings = owner.singing is { Offered: true };
            var result = call.Name switch
            {
                SongTools.SingName => sings ? SingSong(Volatile.Read(ref owner.lastReply), configured, call, afterReply: true)
                    : new(SongTools.Unavailable("singing isn't set up."), true),
                SongTools.PlayName => sings ? await PlaySongAsync(configured, call, token).ConfigureAwait(false)
                    : new(SongTools.Unavailable("singing isn't set up."), true),
                PictureTools.DrawName => DrawAfterReply(configured, call),
                CreationTools.ListName => owner.Creations.ToolKinds.Count > 0 ? ListCreations(call) : new("Martlet keeps no creations here.", true),
                CreationTools.PerformName => owner.Creations.ToolKinds.Count > 0 ? await PerformCreationAsync(call, token).ConfigureAwait(false)
                    : new("Martlet keeps no creations here.", true),
                _ => new ConversationToolResult($"This set has no tool called {call.Name}.", true)
            };
            var told = CreationsCheckIn.TellUser(result) && owner.BringUp(context.CheckInName, result.Output) is not null;
            ErrorLog.Info($"Check-ins: {call.Name} by the check-in {context.CheckInName} {(result.IsError ? "wasn't done" : "ran")}" +
                (told ? "; Martlet tells you why on its own." : "."));
            return new($"{call.Name}: {(result.IsError ? "not done" : "done")}{(told ? "; Martlet tells the user why on its own" : "")}.\n" +
                result.Output + (told ? "\nMartlet tells the user itself; do nothing more about it." : ""), result.IsError);
        }

        // draw_picture for a check-in: the reply already told the user, so the started answer asks nothing more.
        private ConversationToolResult DrawAfterReply(LiveConversationConfiguration configured, TextToolCall call)
        {
            var (arguments, problem) = PictureTools.Parse(call.ArgumentsJson);
            if (arguments is null)
            {
                owner.tools?.Record("Martlet", PictureTools.DrawName, "invalid arguments", "", true);
                return new(problem!, true);
            }
            return StartPicture(null, configured, arguments, PictureTools.DrawName, then: null);
        }

        /// <summary>The talk window stops the song: Stop and Esc quickly (a 300 ms fade), the talk button musically. The note says
        /// which.</summary>
        internal SongStopRecord? StopSong(bool musical, string button) =>
            owner.singing?.Stop(SongStopCause.Button, musical, reason: button);

        // The song job's own text runtime (for writing lyrics), so a think and a song can each have one request at a time.
        private ConversationRuntime SongRuntime()
        {
            lock (owner.gate)
            {
                ObjectDisposedException.ThrowIf(owner.disposed, owner);
                return songRuntime ??= owner.runtimeFactory?.Invoke(songCredentials, owner.clock) ??
                    ConversationRuntime.Create(songCredentials, clock: owner.clock, hostText: new HostTextClient());
            }
        }
    }
}
