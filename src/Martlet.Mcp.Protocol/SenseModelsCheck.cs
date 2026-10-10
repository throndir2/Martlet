using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Conversation;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Mcp;

/// <summary>sense_models_status and sense_models_check: the image and audio models (docs/SENSE_MODELS.md). The status reads a
/// data directory's choices (sense-models.json), the Thinking route and model-abilities.json, and says where pictures and
/// recordings go now (in Thinking's own request, to a model of its own that puts them into words, or nowhere) and why, with the
/// desktop's sense-models-status.json (the lanes' recent jobs: purposes, outcomes and times, never what was sent or said). The
/// check rehearses the production routing (<see cref="SenseRouting"/>) over the combinations of text, image and audio models,
/// the settings file, and the production lanes (<see cref="SenseLanes"/>) with a simulated runner, NOT models. Nothing leaves
/// the process.</summary>
internal static class SenseModelsCheck
{
    // ---------- sense_models_status ----------

    internal static async Task<object> StatusAsync(string dataDirectory, CancellationToken cancellation)
    {
        var loaded = await new SettingsStore(dataDirectory).LoadAsync(cancellation);
        var thinking = loaded.Settings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        var abilities = ModelAbilities.Load(dataDirectory);
        // The Vision and Hearing lists (pools-local.json) when there are any, else sense-models.json; nothing is written here (the
        // desktop makes the lists once). Members kept for some companion PCs are left out: MCP isn't one of them.
        var setup = SenseSetup.Load(dataDirectory, "", abilities, migrate: false);
        var senses = setup.Senses;
        var hosts = SenseLists.ReadHosts(dataDirectory);
        var keys = PoolKeys.Load(dataDirectory);
        return new
        {
            file = setup.State,
            thinking = thinking is null ? null : new
            {
                model = thinking.ModelId, route = thinking.RouteType?.ToString(),
                sees = SenseRouting.ThinkingSees(thinking, abilities).ToString(), hears = SenseRouting.ThinkingHears(thinking, abilities).ToString()
            },
            oneModel = senses.OneModel, allThinking = senses.AllThinking,
            senses = new[] { SenseKind.Image, SenseKind.Audio }.Select(kind =>
            {
                var chosen = senses.For(kind);
                var route = SenseRouting.For(kind, senses, thinking, abilities);
                var own = chosen.Own;
                var found = own is { ModelId: { } id } ? abilities.Find(own.Place == DeepThinkingPlace.Host ? own.HostOrigin : own.Origin, id) : null;
                var area = SenseLists.Area(kind);
                return new
                {
                    kind = kind.ToString(), source = chosen.Source.ToString(), chosenAt = chosen.ChosenAt,
                    own = own is null ? null : new
                    {
                        where = own.Describe(), place = own.Place.ToString(), model = own.ModelId, hostId = own.HostId, onThisPc = own.OnThisPc,
                        ownKey = own.CredentialId is not null, usesThinkingKey = own.UsesThinkingKey(thinking),
                        sees = SenseRouting.Sees(own, abilities).ToString(), hears = SenseRouting.Hears(own, abilities).ToString(),
                        found = found is null ? null : new { sees = found.Sees, hears = found.Hears, source = found.Source, checkedAt = found.CheckedAt }
                    },
                    path = route.Path.ToString(), model = route.Model?.Describe(), unknown = route.Unknown, why = route.Why,
                    // Companion › Vision's or Hearing's list as saved (null: none yet), each member with its model and whether
                    // pictures and recordings may go there; then the places a job tries. The desktop also leaves out members on the
                    // conversation's own computer and graphics card (desktop.file.senses[].pool).
                    list = setup.List(kind)?.Members.Select((m, i) => new
                    {
                        position = i, key = m.Key, kind = m.Kind.ToString(), on = !m.Off, model = SenseLists.ModelOf(m),
                        engine = m.Setting(PoolSettingKeys.Engine), mayReceive = SenseLists.MayReceive(m, area), consented = m.Consented(area.Id),
                        usable = !m.Off && SenseLists.MayReceive(m, area) && SenseLists.Model(m, area, id => hosts.GetValueOrDefault(id), keys) is not null
                    }),
                    pool = route is { Described: true, Model: { } chosenModel } ? new
                    {
                        lane = SensePool.Lane(kind),
                        members = SensePool.Members(kind, chosenModel, setup.For(kind), thinking, abilities)
                            .Select((m, i) => new { position = i, key = m.Key, name = m.Describe(), place = m.Place.ToString(), chosen = i == 0 })
                    } : null
                };
            }),
            desktop = DesktopStatus(dataDirectory)
        };
    }

    private static object DesktopStatus(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "sense-models-status.json");
        try
        {
            if (!File.Exists(path)) return new { state = "none", why = "The desktop hasn't run a conversation with this data directory." };
            if (new FileInfo(path).Length > 65_536) return new { state = "unreadable", why = "sense-models-status.json is too large." };
            return new { state = "loaded", file = JsonNode.Parse(File.ReadAllText(path)) };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new { state = "unreadable", why = error.GetType().Name };
        }
    }

    // ---------- sense_models_check ----------

    private sealed record Step(string Name, bool Passed, string Detail);

    private const string Ollama = GenerationSupport.LocalOllamaChatBaseUrl;

    internal static async Task<object> RunAsync(CancellationToken cancellation)
    {
        var watch = Stopwatch.StartNew();
        List<Step> steps = [];
        void Check(string name, bool passed, string detail) => steps.Add(new(name, passed, detail));
        string Paths(SenseModels senses, SetupRoute thinking, ModelAbilities? abilities = null) =>
            $"pictures {PathOf(SenseKind.Image, senses, thinking, abilities)}, recordings {PathOf(SenseKind.Audio, senses, thinking, abilities)}";

        // Fixture models, by name only (nothing is sent): Gemma 4 E2B sees and hears, Qwen3 8B is text-only, Qwen2.5-VL sees,
        // Gemma 3n hears but doesn't see.
        var omni = Chat("gemma4:e2b");
        var textOnly = Chat("qwen3:8b");
        var eyes = Own(Endpoint("qwen2.5vl:7b"));
        var ears = Own(Endpoint("gemma3n:e4b"));
        var sameAsOther = new SenseModel { Source = SenseSource.OtherSense };

        // 1. The defaults: Thinking takes both itself, as before this setting existed.
        {
            var senses = new SenseModels();
            var paths = Paths(senses, omni);
            Check("defaults-omni", paths == "pictures Thinking, recordings Thinking" && senses.AllThinking, paths);
        }
        // 2. A text-only Thinking model with the defaults: no pictures, transcript only.
        {
            var paths = Paths(new SenseModels(), textOnly);
            Check("defaults-text-only", paths == "pictures None, recordings None", paths);
        }
        // 3. Text and audio the same model, a different image model: recordings go to Thinking, pictures are described.
        {
            var paths = Paths(new SenseModels { Image = eyes }, omni);
            Check("image-model-only", paths == "pictures Described, recordings Thinking", paths);
        }
        // 4. A text-only Thinking model with an image model and an audio model of their own.
        {
            var paths = Paths(new SenseModels { Image = eyes, Audio = ears }, textOnly);
            Check("separate-image-and-audio", paths == "pictures Described, recordings Described", paths);
        }
        // 5. The audio model is the same as the image model, which can't hear: recordings go nowhere, and the reason says why.
        {
            var senses = new SenseModels { Image = eyes, Audio = sameAsOther };
            var audio = SenseRouting.For(SenseKind.Audio, senses, textOnly, null);
            Check("audio-same-as-image", audio is { Path: SensePath.None } && audio.Model?.ModelId == "qwen2.5vl:7b" && audio.Why.Contains("can't hear"),
                $"{audio.Path}: {audio.Why}");
        }
        // 6. One model for both kinds, of its own: Nemotron 3 Nano Omni sees and hears.
        {
            var both = Own(Endpoint("nvidia/nemotron-3-nano-omni-30b-a3b-reasoning", "https://integrate.api.nvidia.com/v1"));
            var senses = new SenseModels { Image = both, Audio = sameAsOther };
            var paths = Paths(senses, textOnly);
            Check("one-model-for-both", paths == "pictures Described, recordings Described" && senses.OneModel, paths);
        }
        // 7. "Same as the other kind" both ways reads as the text model.
        {
            var paths = Paths(new SenseModels { Image = sameAsOther, Audio = sameAsOther }, omni);
            Check("same-as-both-ways", paths == "pictures Thinking, recordings Thinking", paths);
        }
        // 8. A model of its own that is exactly Thinking's endpoint and model is Thinking itself.
        {
            var paths = Paths(new SenseModels { Image = Own(Endpoint("gemma4:e2b")) }, omni);
            Check("own-model-is-thinking", paths == "pictures Thinking, recordings Thinking", paths);
        }
        // 9. A paired computer's model sees for pictures; recordings that follow it go nowhere (it doesn't hear), and a paired
        //    computer's model that hears takes recordings through its gateway.
        {
            var host = Own(Host("diva", "qwen2.5vl:7b"));
            var paths = Paths(new SenseModels { Image = host, Audio = sameAsOther }, textOnly);
            var hearing = SenseRouting.For(SenseKind.Audio, new SenseModels { Audio = Own(Host("ripley", "gemma4:e4b")) }, textOnly, null);
            Check("paired-computer", paths == "pictures Described, recordings None" && hearing.Path == SensePath.Described,
                $"{paths}; an audio model on a paired computer that hears: {hearing.Path}");
        }
        // 10. What Martlet found out about a model wins over its name: a refused picture, then a model it can't tell about.
        {
            var abilities = new ModelAbilities().With(new()
            {
                Origin = Ollama, ModelId = "qwen2.5vl:7b", Sees = false, Source = "a refused picture", CheckedAt = DateTimeOffset.UtcNow
            });
            var refused = SenseRouting.For(SenseKind.Image, new SenseModels { Image = eyes }, textOnly, abilities);
            var unknown = SenseRouting.For(SenseKind.Image, new SenseModels { Image = Own(Endpoint("my-own-model")) }, textOnly, null);
            Check("abilities-win", refused.Path == SensePath.None && unknown is { Path: SensePath.Described, Unknown: true },
                $"refused: {refused.Path}; unknown model: {unknown.Path} (tried)");
        }
        // 11. sense-models.json round trip, and an unreadable file reads as the text model for both. Then the lists are made from it
        //     once (Vision and Hearing, pools-local.json), read back without writing, and a damaged list file reads as the text model.
        {
            var directory = Path.Combine(Path.GetTempPath(), "martlet-senses-" + Guid.NewGuid().ToString("N"));
            try
            {
                var saved = new SenseModels { Image = eyes, Audio = sameAsOther };
                var wrote = saved.Save(directory);
                var (loaded, state) = SenseModels.Read(directory);
                var made = SenseSetup.Load(directory, "pc", null, migrate: true);
                var again = SenseSetup.Load(directory, "pc", null, migrate: false);
                var lists = string.Join(" | ", new[] { made.ImageList, made.AudioList }.Select(l =>
                    $"{l?.Area}: {string.Join(", ", l?.Members.Select(m => $"{m.Key} {SenseLists.ModelOf(m)}") ?? [])}"));
                Check("lists-made-once", made.State == "lists" && again.State == "lists" &&
                    made.ImageList?.Members.SingleOrDefault() is { Kind: Martlet.Core.Cluster.PoolMemberKind.ThisPc } &&
                    made.AudioList?.Members.SingleOrDefault()?.Setting(Martlet.Core.Cluster.PoolSettingKeys.Model) == "qwen2.5vl:7b" &&
                    again.Senses.Place(SenseKind.Image)?.Key == eyes.Own!.Key,
                    $"made {made.State}: {lists}; read again: {again.State}");
                File.WriteAllText(Path.Combine(directory, Martlet.Core.Cluster.PoolSettings.LocalFileName), "{ not json");
                var damaged = SenseSetup.Load(directory, "pc", null, migrate: true);
                File.WriteAllText(Path.Combine(directory, SenseModels.FileName), "{ not json");
                var (broken, brokenState) = SenseModels.Read(directory);
                Check("settings-file", wrote && state == "loaded" && loaded.Place(SenseKind.Audio)?.ModelId == "qwen2.5vl:7b" &&
                    brokenState == "unreadable" && broken.AllThinking && damaged is { State: "unreadable", Senses.AllThinking: true },
                    $"saved {wrote}, read {state}; a broken file reads {brokenState} as the text model; a damaged list file reads {damaged.State}");
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        // The lanes (SenseLanes) with a simulated runner, NOT a model.
        var picture = new BoundedImage([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0], ImageMediaType.Png, 1, 1);
        SenseJob Job(string purpose, string? key = null, int priority = 0, TimeSpan? timeout = null) => new()
        {
            Purpose = purpose, Key = key, Priority = priority, Instructions = "Describe the picture.", Text = "fixture", Image = picture,
            Timeout = timeout ?? TimeSpan.FromSeconds(10)
        };
        var route = SenseRouting.For(SenseKind.Image, new SenseModels { Image = eyes }, textOnly, null);
        // 12. A kind without a model of its own answers NoModel at once, without a request.
        {
            var asked = 0;
            var lanes = new SenseLanes(_ => SenseRouting.For(SenseKind.Image, new SenseModels(), omni, null), (_, _, _, _) =>
            {
                asked++;
                return Task.FromResult(SenseAnswer.Done("x"));
            });
            var result = await lanes.RunAsync(SenseKind.Image, Job("reply picture"), cancellation);
            Check("lanes-no-model", result.Outcome == SenseJobOutcome.NoModel && asked == 0, $"{result.Outcome}, {asked} request(s)");
        }
        // 13. One job at a time; a newer picture replaces the one waiting with its key; waiting jobs start highest priority first.
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var order = new List<string>();
            var lanes = new SenseLanes(_ => route, async (_, _, job, token) =>
            {
                lock (order) order.Add(job.Purpose);
                if (job.Purpose == "first") await gate.Task.WaitAsync(token);
                return SenseAnswer.Done(job.Purpose + " described");
            });
            var first = lanes.RunAsync(SenseKind.Image, Job("first"), cancellation);
            await WaitAsync(() => lanes.Status()[0].Busy, cancellation);
            var summary = lanes.RunAsync(SenseKind.Image, Job("summary", priority: 1), cancellation);
            var older = lanes.RunAsync(SenseKind.Image, Job("older picture", key: "picture", priority: 5), cancellation);
            await WaitAsync(() => lanes.Status()[0].Waiting == 2, cancellation);
            var newer = lanes.RunAsync(SenseKind.Image, Job("newer picture", key: "picture", priority: 5), cancellation);
            var replaced = await older;
            gate.SetResult();
            var results = await Task.WhenAll(first, summary, newer);
            Check("lanes-order", replaced.Outcome == SenseJobOutcome.Stale && results.All(r => r.Succeeded) &&
                string.Join(", ", order) == "first, newer picture, summary",
                $"ran {string.Join(", ", order)}; the older picture: {replaced.Outcome} ({replaced.Problem})");
        }
        // 14. A job whose lane isn't free in time is dropped (Stale); the runner's refusal, failure and silence end as such.
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<SenseAnswer> Run(SenseKind kind, DeepThinkingSettings model, SenseJob job, CancellationToken token)
            {
                switch (job.Purpose)
                {
                    case "busy":
                        await gate.Task.WaitAsync(token);
                        return SenseAnswer.Done("done");
                    case "refused": return SenseAnswer.Rejected("it refused the picture");
                    case "failed": return SenseAnswer.Failed("it failed");
                    default:
                        await Task.Delay(Timeout.Infinite, token);
                        return SenseAnswer.Done("late");
                }
            }
            var lanes = new SenseLanes(_ => route, Run);
            var busy = lanes.RunAsync(SenseKind.Image, Job("busy"), cancellation);
            await WaitAsync(() => lanes.Status()[0].Busy, cancellation);
            var stale = await lanes.RunAsync(SenseKind.Image, Job("waits", timeout: TimeSpan.FromMilliseconds(150)), cancellation);
            gate.SetResult();
            await busy;
            var refused = await lanes.RunAsync(SenseKind.Image, Job("refused"), cancellation);
            var failed = await lanes.RunAsync(SenseKind.Image, Job("failed"), cancellation);
            var silent = await lanes.RunAsync(SenseKind.Image, Job("silent", timeout: TimeSpan.FromMilliseconds(150)), cancellation);
            var status = lanes.Status()[0];
            Check("lanes-outcomes", stale.Outcome == SenseJobOutcome.Stale && refused.Outcome == SenseJobOutcome.Refused &&
                failed.Outcome == SenseJobOutcome.Failed && silent.Outcome == SenseJobOutcome.TimedOut && status is { Busy: false, Waiting: 0, Runs: 4 },
                $"stale {stale.Outcome}, refused {refused.Outcome}, failed {failed.Outcome}, silent {silent.Outcome}; {status.Runs} runs, busy {status.Busy}");
        }
        // 15. A job must carry what its kind takes: a picture for the image model, a recording for the audio model.
        {
            var lanes = new SenseLanes(_ => route, (_, _, _, _) => Task.FromResult(SenseAnswer.Done("x")));
            var wrong = await Throws(() => lanes.RunAsync(SenseKind.Audio, Job("a picture for the audio model"), cancellation));
            Check("lanes-kind", wrong, "a picture sent to the audio lane is refused before any request");
        }
        // 16. One model for both kinds shares one lane: a recording waits while that model describes a picture.
        {
            var both = Own(Endpoint("nvidia/nemotron-3-nano-omni-30b-a3b-reasoning", "https://integrate.api.nvidia.com/v1"));
            var senses = new SenseModels { Image = both, Audio = sameAsOther };
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var order = new List<string>();
            var lanes = new SenseLanes(kind => SenseRouting.For(kind, senses, textOnly, null), async (kind, _, job, token) =>
            {
                lock (order) order.Add(kind.ToString());
                if (kind == SenseKind.Image) await gate.Task.WaitAsync(token);
                return SenseAnswer.Done("described");
            });
            var image = lanes.RunAsync(SenseKind.Image, Job("glance"), cancellation);
            await WaitAsync(() => lanes.Status()[0].Busy, cancellation);
            var audio = lanes.RunAsync(SenseKind.Audio, new SenseJob
            {
                Purpose = "your voice", Instructions = "Describe the recording.", Text = "fixture", Audio = Recording()
            }, cancellation);
            await WaitAsync(() => lanes.Status()[1].Waiting == 1, cancellation);
            var waited = lanes.Status()[1] is { Busy: true, Waiting: 1 } && order.Count == 1;
            gate.SetResult();
            var results = await Task.WhenAll(image, audio);
            Check("lanes-one-model", waited && results.All(r => r.Succeeded) && string.Join(", ", order) == "Image, Audio",
                $"the recording waited for the picture: {waited}; ran {string.Join(", ", order)}");
        }
        // 17. The conversation comes first: a job waits while a reply holds the model's hardware and runs after; a running job is
        //     stopped when a reply starts; a job the hold outlasts is dropped.
        {
            var holding = 1;
            var lanes = new SenseLanes(_ => route, async (_, _, job, token) =>
            {
                if (job.Purpose == "long") await Task.Delay(Timeout.Infinite, token);
                return SenseAnswer.Done("described");
            }, held: _ => Volatile.Read(ref holding) == 1);
            var waiting = lanes.RunAsync(SenseKind.Image, Job("reply picture"), cancellation);
            await WaitAsync(() => lanes.Status()[0].Held == 1, cancellation);
            var heldThen = lanes.Status()[0].Held;
            Volatile.Write(ref holding, 0);
            var ranAfter = await waiting;
            var running = lanes.RunAsync(SenseKind.Image, Job("long"), cancellation);
            await WaitAsync(() => lanes.Status()[0].Busy, cancellation);
            await Task.Delay(60, cancellation);
            Volatile.Write(ref holding, 1);
            var stopped = await running;
            var outlasted = await lanes.RunAsync(SenseKind.Image, Job("summary", timeout: TimeSpan.FromMilliseconds(150)), cancellation);
            Check("lanes-hold", heldThen == 1 && ranAfter.Succeeded && stopped.Outcome == SenseJobOutcome.Preempted &&
                outlasted.Outcome == SenseJobOutcome.Stale && lanes.Status()[0].Held == 0,
                $"held {heldThen}, then {ranAfter.Outcome}; a running job when a reply started: {stopped.Outcome}; a job the hold outlasted: " +
                $"{outlasted.Outcome} ({outlasted.Problem})");
        }
        // 18. A background job (a helper, priority below zero) gives way to a reply's picture and starts again after it; a summary
        //     (priority 0) waits behind it instead.
        {
            var ran = new List<string>();
            var helperRuns = 0;
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lanes = new SenseLanes(_ => route, async (_, _, job, token) =>
            {
                lock (ran) ran.Add(job.Purpose);
                if (job.Purpose == "touch_zones" && Interlocked.Increment(ref helperRuns) == 1) await Task.Delay(Timeout.Infinite, token);
                if (job.Purpose == "eyes") await gate.Task.WaitAsync(token);
                return SenseAnswer.Done(job.Purpose + " done");
            });
            var helper = lanes.RunAsync(SenseKind.Image, Job("touch_zones", priority: -10) with { DropWhenStale = false }, cancellation);
            await WaitAsync(() => ran.Count == 1, cancellation);
            var reply = await lanes.RunAsync(SenseKind.Image, Job("reply picture", key: "picture", priority: 10), cancellation);
            var helped = await helper;
            var measuring = lanes.RunAsync(SenseKind.Image, Job("eyes", priority: -10) with { DropWhenStale = false }, cancellation);
            await WaitAsync(() => ran.Count == 4, cancellation);
            var summary = lanes.RunAsync(SenseKind.Image, Job("summary", key: "screen-summary"), cancellation);
            await WaitAsync(() => lanes.Status()[0].Waiting == 1, cancellation);
            var waited = ran.Count == 4;
            gate.SetResult();
            var both = await Task.WhenAll(measuring, summary);
            Check("lanes-yield", reply.Succeeded && helped.Succeeded && waited && both.All(r => r.Succeeded) &&
                string.Join(", ", ran) == "touch_zones, reply picture, touch_zones, eyes, summary",
                $"ran {string.Join(", ", ran)}; the summary waited behind the eyes: {waited}");
        }

        // The pools (SensePool) with simulated members on a queue of their own, NOT models.
        var diva = Host("diva", "qwen2.5vl:7b");
        var ripley = Host("ripley", "qwen2.5vl:7b");
        var kirk = Host("kirk", "qwen2.5vl:7b");
        Task<(SenseAnswer Answer, SensePoolRoute Route)> Pool(Func<DeepThinkingSettings, SenseAttempt> answer, TimeSpan? until = null) =>
            SensePool.RunAsync(SenseKind.Image, [diva, ripley, kirk], (member, _) => Task.FromResult(answer(member)),
                DateTimeOffset.UtcNow + (until ?? TimeSpan.FromSeconds(5)), null, cancellation,
                new Martlet.Core.Cluster.WorkQueue { Retry = TimeSpan.FromMilliseconds(10) });
        // 19. Who is a member: the Vision list's members in order (SenseLists.Models), each as the place a job goes; left out: a
        //     computer not paired here, a cloud provider without the owner's agreement, a server off this PC not allowed pictures, a
        //     member that is off or kept for another companion PC. The chosen model is the first that isn't known not to see; after
        //     it, a model known not to see and the conversation's own Thinking model are left out.
        {
            var area = Martlet.Core.Cluster.PoolAreas.Vision;
            static Martlet.Core.Cluster.PoolMember With(Martlet.Core.Cluster.PoolMember m, string model) =>
                m.WithSetting(Martlet.Core.Cluster.PoolSettingKeys.Model, model);
            var pairing = new SenseHost("diva", "https://10.0.0.5:9443/", "sha256:" + new string('0', 64), "device", Guid.NewGuid());
            var agreed = Martlet.Core.Cluster.PoolMember.Cloud("openrouter", "qwen/qwen2.5-vl-72b-instruct", "https://openrouter.ai/api/v1");
            var list = new Martlet.Core.Cluster.PoolList
            {
                Area = area.Id,
                Members =
                [
                    With(Martlet.Core.Cluster.PoolMember.Computer("not-paired"), "qwen2.5vl:7b"),
                    With(Martlet.Core.Cluster.PoolMember.Computer("diva"), "qwen3:8b"),
                    Martlet.Core.Cluster.PoolMember.Cloud("openai", "gpt-4o", "https://api.openai.com/v1"),
                    With(Martlet.Core.Cluster.PoolMember.Service("https://192.168.1.9:8443/v1"), "qwen2.5vl:7b"),
                    With(Martlet.Core.Cluster.PoolMember.Gpu("diva", 2), "qwen2.5vl:7b"),
                    With(Martlet.Core.Cluster.PoolMember.Service("https://192.168.1.11:8443/v1"), "qwen2.5vl:7b") with { Off = true },
                    With(Martlet.Core.Cluster.PoolMember.Computer("kept"), "qwen2.5vl:7b") with { OnlyFor = ["other-pc"] },
                    agreed.WithConsent(area.Id, DateTimeOffset.UtcNow),
                    With(Martlet.Core.Cluster.PoolMember.Service("https://192.168.1.10:8443/v1"), "qwen2.5vl:7b").WithSetting(SenseLists.MediaSetting, SenseLists.Allowed),
                    With(Martlet.Core.Cluster.PoolMember.ThisPc(), "gemma4:e2b")
                ]
            };
            var models = SenseLists.Models(area, list, "pc", id => id == "diva" ? pairing : null, new Martlet.Core.Cluster.PoolKeys());
            var chosen = SensePool.Chosen(SenseKind.Image, models, null);
            var members = chosen is null ? [] : SensePool.Members(SenseKind.Image, chosen, models, omni, null).Select(m => m.Key).ToArray();
            string[] expected = ["host:diva", "host:diva#gpu2", "endpoint:https://openrouter.ai/api/v1|qwen/qwen2.5-vl-72b-instruct",
                "endpoint:https://192.168.1.10:8443/v1|qwen2.5vl:7b", "endpoint:http://127.0.0.1:11434/v1|gemma4:e2b"];
            Check("pool-members", models.Select(m => m.Key).SequenceEqual(expected) && chosen?.Key == "host:diva#gpu2" &&
                members.SequenceEqual(expected[1..4]),
                $"places: {string.Join(", ", models.Select(m => m.Key))}; chosen: {chosen?.Key}; a job tries: {string.Join(", ", members)}");
        }
        // 20. A free chosen model takes the job with one request and no wait.
        {
            var asked = 0;
            var (answer, taken) = await Pool(_ =>
            {
                asked++;
                return SenseAttempt.Done(SenseAnswer.Done("described"));
            });
            Check("pool-first-free", answer.Text == "described" && asked == 1 && taken is { Position: 0, Busy: 0, Unavailable: 0 },
                $"{asked} request(s), taken by member {taken.Position} after {taken.Waited.TotalMilliseconds:0} ms");
        }
        // 21. A busy chosen model and an unreachable member pass the job on at once.
        {
            var (answer, taken) = await Pool(member => member.HostId switch
            {
                "diva" => SenseAttempt.Busy("diva is busy"),
                "ripley" => SenseAttempt.Unavailable("ripley is offline"),
                _ => SenseAttempt.Done(SenseAnswer.Done("kirk described it"))
            });
            Check("pool-busy-next", answer.Text == "kirk described it" && taken is { Position: 2, Busy: 1, Unavailable: 1, Elsewhere: true },
                $"taken by {taken.Name} (member {taken.Position}); {taken.Busy} busy, {taken.Unavailable} unavailable");
        }
        // 22. Every member busy: the job waits and the first to free takes it.
        {
            var freed = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(120);
            var (answer, taken) = await Pool(member => member.HostId == "ripley" && DateTimeOffset.UtcNow >= freed
                ? SenseAttempt.Done(SenseAnswer.Done("ripley described it")) : SenseAttempt.Busy($"{member.HostId} is busy"));
            Check("pool-all-busy-waits", answer.Text == "ripley described it" && taken.Position == 1 && taken.Waited >= TimeSpan.FromMilliseconds(80),
                $"taken by {taken.Name} after waiting {taken.Waited.TotalMilliseconds:0} ms ({taken.Busy} busy tries)");
        }
        // 23. A member held for a live turn is passed over; a pool held everywhere ends at once, without words.
        {
            var (answer, taken) = await Pool(member => member.HostId == "diva"
                ? SenseAttempt.Held("diva keeps its graphics card for a live conversation") : SenseAttempt.Done(SenseAnswer.Done("ripley described it")));
            var clock = Stopwatch.StartNew();
            var (held, _) = await Pool(member => SenseAttempt.Held($"{member.HostId} keeps its graphics card"));
            Check("pool-held", answer.Text == "ripley described it" && taken.Position == 1 && held.Text is null &&
                held.Problem?.Contains("live conversation") == true && clock.Elapsed < TimeSpan.FromSeconds(2),
                $"taken by {taken.Name}; held everywhere: {held.Problem} after {clock.ElapsedMilliseconds} ms");
        }

        return new
        {
            passed = steps.All(s => s.Passed), steps, milliseconds = watch.ElapsedMilliseconds,
            note = "Routing, lanes and pools rehearsed in process with simulated runners and members, NOT models; nothing was sent."
        };
    }

    private static string PathOf(SenseKind kind, SenseModels senses, SetupRoute thinking, ModelAbilities? abilities) =>
        SenseRouting.For(kind, senses, thinking, abilities).Path.ToString();

    private static SetupRoute Chat(string model, string origin = Ollama) => new()
    {
        RouteType = SetupRouteType.ChatCompletions, Role = SetupRole.Llm, ProviderAlias = ChatCompletionsSetup.Alias, Origin = origin,
        ModelId = model, ConfigurationRevision = Guid.NewGuid(), Enabled = true
    };

    private static DeepThinkingSettings Endpoint(string model, string origin = Ollama) =>
        new() { Place = DeepThinkingPlace.Endpoint, Origin = origin, ModelId = model };

    private static DeepThinkingSettings Host(string host, string model) => new()
    {
        Place = DeepThinkingPlace.Host, ModelId = model, HostId = host, HostOrigin = $"https://{host}.local:9443",
        HostSpkiFingerprint = "sha256:" + new string('0', 64), HostDeviceId = "device", HostCredentialId = Guid.NewGuid()
    };

    private static SenseModel Own(DeepThinkingSettings model) => new() { Source = SenseSource.Own, Own = model };

    // Half a second of silence, as a WAV: the audio lane's fixture recording.
    private static BoundedWaveAudio Recording() =>
        BoundedWaveAudio.FromPcm(new Martlet.Core.Audio.PcmFormat { SampleRate = 16_000, Channels = 1, Encoding = Martlet.Core.Audio.PcmEncoding.Signed16LittleEndian },
            new byte[16_000]);

    private static bool Refuses(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ContractException) { return true; }
    }

    private static async Task<bool> Throws(Func<Task> action)
    {
        try
        {
            await action();
            return false;
        }
        catch (ContractException) { return true; }
    }

    private static async Task WaitAsync(Func<bool> condition, CancellationToken cancellation)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10, cancellation);
    }
}
