using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Installation;
using Martlet.Doctor;
using Martlet.Mcp.Client;
using Martlet.Mcp.Shared;
using Martlet.Core.Settings;

namespace Martlet.Mcp;

internal sealed partial class McpServer
{
    /// <summary>The character profiles saved in a data directory (Companion › Profiles), by key, with whether each part
    /// resolves here and which one matches what Martlet uses now. Names are the owner's and are never returned.</summary>
    private static object CharacterProfiles(JsonElement arguments)
    {
        var directory = DataDirectory(arguments);
        var settingsPath = Path.Combine(directory, "settings.json");
        var settings = File.Exists(settingsPath) ? Martlet.Core.Settings.SettingsJson.Read(File.ReadAllBytes(settingsPath)) : null;
        var companion = settings?.Companion;
        var shownPath = Martlet.Avatar.Hosting.AvatarProfileStore.Peek(directory)?.ModelPath;

        var library = Martlet.Avatar.Hosting.SharedCharacterModels.Load(directory) ?? Martlet.Core.Characters.CharacterModelLibrary.Empty;
        var modelNow = shownPath is null || Martlet.Avatar.Hosting.BundledLive2D.IsBuiltIn(shownPath)
            ? Martlet.Core.Settings.CharacterProfile.BuiltInModel
            : Martlet.Avatar.Hosting.SharedCharacterModels.ForPath(directory, library, shownPath)?.Id;
        Martlet.Core.Voices.SpeakingVoiceLibrary voices;
        try { voices = Martlet.Core.Voices.SpeakingVoiceLibrary.Parse(File.ReadAllBytes(Path.Combine(directory, "speaking-voices.json"))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException)
        {
            voices = Martlet.Core.Voices.SpeakingVoiceLibrary.Empty.Seed(Martlet.F5.F5SharedVoices.Starters);
        }
        var voiceNow = settings?.Setup?.Routes.FirstOrDefault(r => r.Role == Martlet.Core.Settings.SetupRole.Tts) is
            { RouteType: Martlet.Core.Settings.SetupRouteType.GatewayF5, Reference: { } reference }
            ? reference.ReferenceRevision : voices.ChosenVoice?.Id;
        var current = companion?.CurrentCharacter(modelNow, voiceNow);
        var profiles = companion?.CharacterList ?? [];
        var (hereState, hereInUse, here) = ProfilesHere(directory);
        var sharing = HouseholdSharingStatus.Own(directory);
        return new
        {
            state = settings is null ? "no-settings" : profiles.Count == 0 ? "none" : "loaded",
            count = profiles.Count,
            lastUsed = profiles.FirstOrDefault(c => c.Id == companion!.ActiveCharacterId)?.Key,
            current = current?.Key,
            look = modelNow is null ? "model-file-outside-list" : modelNow == Martlet.Core.Settings.CharacterProfile.BuiltInModel ? "builtin"
                : "shared:" + Martlet.Avatar.Hosting.SharedCharacterModels.Key(modelNow),
            voiceChosen = voiceNow is not null,
            hereState,
            hereInUse = profiles.FirstOrDefault(c => c.Id == hereInUse)?.Key,
            profiles = profiles.Select(p => new
            {
                key = p.Key,
                personaSaved = companion!.Personas.Any(persona => persona.Id == p.PersonaId),
                personaActive = p.PersonaId == companion.ActivePersonaId,
                look = p.ModelId switch
                {
                    null => "keep",
                    Martlet.Core.Settings.CharacterProfile.BuiltInModel => "builtin",
                    var id => library.Live.FirstOrDefault(m => m.Id == id) is { } model
                        ? Martlet.Avatar.Hosting.SharedCharacterModels.IsComplete(directory, model) ? "ready" : "copying"
                        : "missing"
                },
                voice = p.VoiceId is null ? "keep" : voices.Find(p.VoiceId) is { Removed: false } ? "listed" : "missing",
                inUse = p.Id == current?.Id,
                here = here.GetValueOrDefault(p.Id),
                // How the account in use shares it with the household (household_sharing); null without accounts.
                sharing = HouseholdSharingStatus.ModeOf(sharing, p.Id)
            }).ToArray()
        };
    }

    /// <summary>character-profiles-local.json in a data directory, read through its store (CharacterProfileLocalStore, built into
    /// this server from Martlet.Desktop): the profile whose choices this PC uses (InUse) and what each character profile keeps on this
    /// PC, by profile ID: its place (device-independent pixels, the monitor and whether it is locked; null without one), its usual
    /// gaze (<c>personality</c> or a gaze's word), whether replies may change it, and which touches stop it while it talks
    /// (<c>any</c>, <c>intimate</c> or <c>never</c>). No file means no profile keeps anything here.</summary>
    private static (string State, Guid? InUse, Dictionary<Guid, object> Profiles) ProfilesHere(string directory)
    {
        Martlet.Mcp.Shared.CharacterProfilesHere? here;
        try { here = Martlet.Mcp.Shared.CharacterProfileLocalStore.Read(directory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return ("unreadable", null, []);
        }
        if (here is null) return ("none", null, []);
        var profiles = here.Profiles.ToDictionary(entry => entry.Key, entry => (object)new
        {
            place = entry.Value.Placement is { } at
                ? new { locked = at.Locked, left = (double?)at.Left, top = (double?)at.Top, width = (double?)at.Width, height = (double?)at.Height, screen = at.Screen }
                : null,
            gaze = entry.Value.GazeUsual is { } usual ? JsonSerializer.Serialize(usual).Trim('"') : "personality",
            gazeFree = entry.Value.GazeFree,
            touchInterrupts = JsonNamingPolicy.CamelCase.ConvertName(entry.Value.TouchInterrupts.ToString())
        });
        return ("loaded", here.InUse, profiles);
    }

    /// <summary>The shared character models as the desktop keeps them in a data directory (Martlet.Avatar.Hosting's
    /// SharedCharacterModels): keys, renderers, sizes and whether each copy is complete here. Names and paths are the owner's
    /// and are never returned.</summary>
    private static object CharacterModels(JsonElement arguments)
    {
        var directory = DataDirectory(arguments);
        var shownPath = Martlet.Avatar.Hosting.AvatarProfileStore.Peek(directory)?.ModelPath;

        var path = Path.Combine(directory, Martlet.Avatar.Hosting.SharedCharacterModels.LibraryFile);
        Martlet.Core.Characters.CharacterModelLibrary? library = null;
        string state;
        if (!File.Exists(path)) state = "none";
        else
        {
            library = Martlet.Avatar.Hosting.SharedCharacterModels.Load(directory);
            state = library is null ? "unreadable" : "loaded";
        }
        library ??= Martlet.Core.Characters.CharacterModelLibrary.Empty;
        var shown = Martlet.Avatar.Hosting.SharedCharacterModels.ForPath(directory, library, shownPath);
        var shownKey = Martlet.Avatar.Hosting.SharedCharacterModels.KeyOfPath(directory, shownPath);
        var incoming = Path.Combine(directory, Martlet.Avatar.Hosting.SharedCharacterModels.IncomingDirectoryName);
        var root = Martlet.Avatar.Hosting.SharedCharacterModels.Root(directory);
        return new
        {
            state,
            live = library.Live.Count,
            tombstones = library.Models.Count(m => m.Removed),
            totalBytes = library.Live.Sum(m => m.Bytes),
            revision = library.Revision,
            models = library.Live.Select(m => new
            {
                key = Martlet.Avatar.Hosting.SharedCharacterModels.Key(m.Id), renderer = m.Renderer, files = m.Files!.Count,
                pieces = m.Files.Sum(f => f.Chunks.Count), bytes = m.Bytes, addedBy = m.AddedBy, addedAt = m.AddedAt, updatedBy = m.UpdatedBy,
                ready = Martlet.Avatar.Hosting.SharedCharacterModels.IsComplete(directory, m), shown = shown?.Id == m.Id
            }),
            copies = Directory.Exists(root) ? Directory.EnumerateDirectories(root).Count() : 0,
            incoming = Directory.Exists(incoming)
                ? Directory.EnumerateDirectories(incoming).Select(d => new { key = Path.GetFileName(d)[..Math.Min(16, Path.GetFileName(d).Length)],
                    pieces = Directory.EnumerateFiles(d).Count() }).ToArray()
                : [],
            showing = shownPath is null ? "built-in (nothing saved)"
                : Martlet.Avatar.Hosting.BundledLive2D.IsBuiltIn(shownPath) ? "built-in"
                : shown is not null ? "shared:" + Martlet.Avatar.Hosting.SharedCharacterModels.Key(shown.Id)
                : shownKey is not null ? "unlisted-copy:" + shownKey
                : "model-file-outside-list"
        };
    }

    private static object F5Voices(JsonElement arguments)
    {
        var directory = DataDirectory(arguments);
        var starters = Martlet.F5.F5BundledVoices.All.Select(voice =>
        {
            try
            {
                var format = voice.Check();
                return (object)new
                {
                    key = voice.Key, name = voice.Name, female = voice.Female, cute = voice.Cute, description = voice.Description,
                    licence = voice.Licence, transcript = voice.Transcript,
                    sha256 = voice.AudioSha256, sampleRate = format.SampleRate, durationMs = format.DurationMilliseconds, valid = true,
                    // The engines that can clone this clip (GPT-SoVITS needs 3-10 s) and the language its transcript is read in.
                    engines = Martlet.Core.Settings.SpeechEngines.All
                        .Where(engine => Martlet.Core.Settings.SpeechEngines.ReferenceProblem(engine, format.DurationMilliseconds) is null)
                        .Select(engine => engine.Key).ToArray(),
                    language = Martlet.Core.Settings.SpeechEngines.ReferenceLanguage(voice.Transcript)
                };
            }
            catch (Martlet.F5.F5Exception error)
            {
                return new { key = voice.Key, name = voice.Name, valid = false, problem = error.Failure.ToString() };
            }
        }).ToArray();
        // A starter voice's key, a retired recording's key (retired-sample, retired-librivox-annie-anime...) or "own".
        static string Kind(string sha256) => Martlet.F5.F5BundledVoices.ForAudio(sha256)?.Key ??
            Martlet.F5.F5BundledVoices.RetiredForAudio(sha256)?.Key ?? "own";
        static bool Retired(string kind) => Martlet.F5.F5BundledVoices.Retired.Any(voice => voice.Key == kind);
        // A voice's ID is its reference revision, so a starter voice's ID is known even after only its tombstone remains, and so
        // is a former starter voice's.
        var starterIds = Martlet.F5.F5BundledVoices.All.Select(voice => (voice.Key, voice.AudioSha256, voice.Transcript))
            .Concat(Martlet.F5.F5BundledVoices.Retired.Where(voice => voice.Transcript is not null)
                .Select(voice => (voice.Key, voice.AudioSha256, Transcript: voice.Transcript!)))
            .ToDictionary(voice => Martlet.Core.Voices.SpeakingVoiceLibrary.ReferenceId(voice.AudioSha256, voice.Transcript), voice => voice.Key);
        string KindOfId(string id) => starterIds.GetValueOrDefault(id) ?? "own";
        // The shared list (speaking-voices.json, the file Martlet.Desktop's F5Voices keeps; absent until a voice is first used,
        // changed or shared). Own voices are counted, never named.
        object library;
        var libraryPath = Path.Combine(directory, "speaking-voices.json");
        if (!File.Exists(libraryPath)) library = new { state = "none" };
        else
        {
            try
            {
                var shared = Martlet.Core.Voices.SpeakingVoiceLibrary.Parse(File.ReadAllBytes(libraryPath));
                var live = shared.Live;
                library = new
                {
                    state = "loaded", voices = live.Count, revision = shared.Revision,
                    starters = live.Select(v => KindOfId(v.Id)).Where(kind => kind != "own" && !Retired(kind)).ToArray(),
                    // Former starter voices still listed (an older Martlet's list, until this desktop next brings its voices in step).
                    retired = live.Select(v => KindOfId(v.Id)).Where(Retired).ToArray(),
                    own = live.Count(v => KindOfId(v.Id) == "own"),
                    removed = shared.Voices.Count(v => v.Removed),
                    removedStarters = shared.Voices.Where(v => v.Removed).Select(v => KindOfId(v.Id)).Where(kind => kind != "own").ToArray(),
                    chosen = shared.ChosenVoice is { } chosenVoice ? KindOfId(chosenVoice.Id) : null,
                    chosenBy = shared.ChosenVoice is not null ? shared.Chosen!.UpdatedBy : null,
                    // Voices made from several recordings, by the key the Voices page uses (the ID's first 16 hex digits): how many
                    // recordings, each one's length, the joined length, the engines that can clone it and those that learn from each.
                    severalRecordings = live.Where(v => v.Clips is not null).Select(v => new
                    {
                        key = v.Id[..16], recordings = v.Clips!.Count, clipMs = v.ClipMilliseconds, durationMs = v.DurationMilliseconds,
                        sampleRate = v.SampleRate,
                        engines = Martlet.Core.Settings.SpeechEngines.All
                            .Where(engine => Martlet.Core.Settings.SpeechEngines.ReferenceProblem(engine, v.DurationMilliseconds, v.ClipMilliseconds) is null)
                            .Select(engine => engine.Key).ToArray(),
                        learnsFromEach = Martlet.Core.Settings.SpeechEngines.All
                            .Where(engine => Martlet.Core.Settings.SpeechEngines.UsesClips(engine, v.ClipMilliseconds))
                            .Select(engine => engine.Key).ToArray()
                    }).ToArray()
                };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException)
            {
                library = new { state = "unreadable", problem = error is Martlet.Core.Contracts.ContractException ? error.Message : error.GetType().Name };
            }
        }
        var storeDirectory = Path.Combine(directory, "f5-voices");
        object list;
        if (!File.Exists(Path.Combine(storeDirectory, ".martlet-f5-references.v1.json"))) list = new { state = "none" };
        else
        {
            try
            {
                using var store = Martlet.F5.F5ReferencePresetStore.Open(storeDirectory);
                var inspection = store.Inspect();
                var latest = inspection.Presets.Select(p => (p.Id, Kind: Kind(p.Snapshots.LastOrDefault()?.AudioSha256 ?? ""))).ToArray();
                list = new
                {
                    state = "loaded", voices = latest.Length,
                    starters = latest.Where(p => p.Kind != "own" && !Retired(p.Kind)).Select(p => p.Kind).Distinct().ToArray(),
                    own = latest.Count(p => p.Kind == "own"),
                    retired = latest.Where(p => Retired(p.Kind)).Select(p => p.Kind).Distinct().ToArray(),
                    applied = latest.Where(p => p.Id == inspection.AppliedPresetId).Select(p => p.Kind).FirstOrDefault()
                };
            }
            catch (Martlet.F5.F5Exception error) { list = new { state = error.Failure == Martlet.F5.F5Failure.Busy ? "busy" : "unreadable", problem = error.Failure.ToString() }; }
        }
        // The voice the speaking (TTS) route keeps, which is what F5 actually speaks with.
        object speaking;
        var settingsPath = Path.Combine(directory, "settings.json");
        if (!File.Exists(settingsPath)) speaking = new { state = "none" };
        else
        {
            try
            {
                var settings = Martlet.Core.Settings.SettingsJson.Read(File.ReadAllBytes(settingsPath));
                var route = settings.Setup?.Routes.FirstOrDefault(r => r.Role == Martlet.Core.Settings.SetupRole.Tts);
                speaking = new
                {
                    state = "loaded", route = route?.RouteType.ToString(),
                    voice = route?.Reference is { } reference ? Kind(reference.AudioSha256) : null,
                    engine = route?.GatewaySnapshot is { } snapshot
                        ? Martlet.Core.Settings.SpeechEngines.ForRoute(snapshot.RouteId)?.Key : null,
                    host = route?.RouteType == Martlet.Core.Settings.SetupRouteType.GatewayF5 ? route.Gateway?.HostId : null,
                    model = route?.RouteType == Martlet.Core.Settings.SetupRouteType.GatewayF5 ? route.ModelId : null
                };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException or JsonException)
            {
                speaking = new { state = "unreadable", problem = error is Martlet.Core.Contracts.ContractException ? error.Message : error.GetType().Name };
            }
        }
        var fallback = Martlet.F5.F5BundledVoices.Default;
        // The self-hosted voice engines (F5-TTS, XTTS-v2, GPT-SoVITS, Dia) and the one chosen on this desktop (speaking-engine.txt, the file
        // Martlet.Core.Settings.SpeakingEngineFile keeps; a speaking route on a host's engine wins over it).
        var chosen = Martlet.Core.Settings.SpeakingEngineFile.ReadKey(directory);
        var engines = Martlet.Core.Settings.SpeechEngines.All.Select(engine => new
        {
            key = engine.Key, name = engine.Name, hostRole = engine.HostRoleKind, routeId = engine.RouteId, path = engine.Path,
            model = engine.DefaultModel, weightsLicence = engine.WeightsLicense, nonCommercial = engine.NonCommercial,
            minimumGpuMemoryGb = engine.MinimumGpuMemoryGb,
            minimumReferenceMs = engine.MinimumReferenceMilliseconds, maximumReferenceMs = engine.MaximumReferenceMilliseconds,
            summary = engine.Summary, languages = engine.Languages, streams = engine.StreamsWhileGenerating, features = engine.Features,
            abilities = Abilities(engine.Abilities, engine.Tags), runsOn = RunsOn(engine.Footprint, engine.RunsOn),
            @default = engine == Martlet.Core.Settings.SpeechEngines.Default,
            supportsTags = engine.SupportsTags, multipleReferences = engine.MultipleReferences,
            tags = engine.Tags.Select(tag => new { text = tag.Text, kind = tag.Kind.ToString(), usage = tag.Usage }).ToArray()
        }).ToArray();
        // The other ways Martlet speaks, with the same rundown Companion › Voice shows (VoiceEngineAbilities-<key>,
        // VoiceEngineRunsOn-<key>).
        var catalog = Martlet.Core.Planning.FootprintCatalog.Default;
        var openAiVoice = catalog.Find(Martlet.Core.Planning.FootprintCatalog.OpenAiVoiceId);
        var otherVoices = new[]
        {
            new { key = "openai", name = "OpenAI voice", abilities = Abilities(Martlet.Core.Settings.VoiceAbilities.OpenAiVoice, []),
                runsOn = RunsOn(openAiVoice, openAiVoice?.WhereItRuns ?? "") }
        };
        return new
        {
            @default = fallback.Key, defaultName = fallback.Name, defaultFemale = fallback.Female, defaultCute = fallback.Cute,
            cute = Martlet.F5.F5BundledVoices.All.Where(voice => voice.Cute).Select(voice => voice.Key).ToArray(), starters,
            retired = Martlet.F5.F5BundledVoices.Retired.Select(voice => new { key = voice.Key, name = voice.Name }).ToArray(), library, list, speaking,
            engines, otherVoices, chosenEngine = Martlet.Core.Settings.SpeechEngines.ForKey(chosen)?.Key ?? Martlet.Core.Settings.SpeechEngines.Default.Key,
            // Chatterbox Original's General and Expressive style as Companion › Voice saved it (Resemble's suggestions until then).
            chatterboxStyle = ChatterboxStyleReport(directory)
        };

        static object ChatterboxStyleReport(string directory)
        {
            var style = Martlet.Core.Settings.ChatterboxStyle.Load(directory);
            return new
            {
                saved = File.Exists(Path.Combine(directory, Martlet.Core.Settings.ChatterboxStyle.FileName)),
                generalExaggeration = style.GeneralExaggeration, generalCfgWeight = style.GeneralCfgWeight,
                expressiveExaggeration = style.ExpressiveExaggeration, expressiveCfgWeight = style.ExpressiveCfgWeight,
                summary = style.Describe()
            };
        }

        // What a voice can do: cloning, sounds and emotions (yes, partly or no), the tags that do it and the rundown's words.
        static object Abilities(Martlet.Core.Settings.VoiceAbilities abilities, IReadOnlyList<Martlet.Core.Settings.VoiceTag> tags) => new
        {
            cloning = abilities.Cloning, sounds = abilities.Sounds, emotions = abilities.Emotions.ToString(),
            items = abilities.Items.Select(item => new
            {
                name = item.Name, level = item.Level.ToString(), note = item.Note, help = item.Help,
                tags = abilities.TagsFor(item, tags).Select(tag => tag.Text).ToArray()
            }).ToArray(),
            summary = abilities.Describe()
        };

        // Where a voice runs: on a GPU (with its graphics memory in GB: typical, most and the smallest card), the CPU or
        // online, as the footprint catalog says, and the line Companion › Voice shows.
        static object RunsOn(Martlet.Core.Planning.ComponentOption? option, string text) => new
        {
            on = option is null ? "gpu" : !option.IsLocal ? "online" : option.UsesGpu ? "gpu" : "cpu",
            vramGb = option?.UsesGpu == true ? option.Usual.VramGb : 0, peakVramGb = option?.UsesGpu == true ? option.GpuGb : 0,
            minimumGpuGb = option?.MinGpuGb ?? 0, evidence = option?.Evidence.ToString(), text
        };
    }
}
