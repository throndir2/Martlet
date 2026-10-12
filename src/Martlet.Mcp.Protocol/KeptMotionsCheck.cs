using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Conversation;
using Martlet.Core.Creations;
using Martlet.Core.Motion;

namespace Martlet.Mcp;

/// <summary>kept_motions_status and kept_motions_check (docs/AVATARS.md#motions-in-conversation). The status reads a data directory's
/// kept motions (the <c>motion</c> creations every VRM character can do; never their prompts' words), whether this computer
/// saw the starter motions made, the body_motion tool and job kind the conversation offers, and, with a VRM model, the kept
/// motions as that model's emotes and motions with their tags. The check makes one motion (or the starter motions) with the
/// FIXTURE - NOT AI motion maker, checks the clip as the VRM renderer does, keeps it as a motion creation, reads it back,
/// shows that the creation tools' words don't change and lists it as a VRM's motion with its reply tag. It never uses a
/// motion computer: motion_check does that (docs/MOTION_HOST.md).</summary>
internal static class KeptMotionsCheck
{
    internal static object Status(string dataDirectory, string? modelPath)
    {
        var creations = CreationsCheck.Folder(dataDirectory);
        var library = CreationStore.View(creations);
        var kept = MotionCreations.Kept(library);
        return new
        {
            fixture = Environment.GetEnvironmentVariable("MARTLET_MOTION_FIXTURE") == "1",
            settingsFile = File.Exists(Path.Combine(dataDirectory, MotionSettings.FileName)),
            offered = "body_motion is offered to replies while motions are set up (motion.json names a paired computer that runs the " +
                "motion role, or MARTLET_MOTION_FIXTURE=1) and the chosen character is a VRM; nothing changes otherwise.",
            tool = new
            {
                name = MotionTools.Definition.Name, description = MotionTools.Definition.Description,
                parameters = JsonNode.Parse(MotionTools.Definition.ParametersJson)
            },
            kind = new
            {
                name = MotionTools.Kind.Name, maxActive = MotionTools.Kind.MaxActive, perHour = MotionTools.Kind.MaxPerHour,
                timeLimitMinutes = MotionTools.Kind.TimeLimit?.TotalMinutes, doing = MotionTools.Kind.Doing, quiet = MotionTools.Kind.Quiet
            },
            creationKind = new
            {
                name = MotionCreations.Kind.Name, forTools = MotionCreations.Kind.ForTools, autoCleanup = MotionCreations.Kind.AutoCleanup,
                maxClipBytes = MotionCreations.MaximumClipBytes
            },
            starters = new
            {
                names = MotionTools.Starters.Select(s => s.Name),
                seenHere = File.Exists(Path.Combine(creations, MotionTools.StartersFile)),
                kept = kept.Count(c => MotionCreations.Metadata(c)?.Starter == true)
            },
            kept = new
            {
                count = kept.Count,
                motions = kept.TakeLast(64).Select(c =>
                {
                    var metadata = MotionCreations.Metadata(c);
                    return new
                    {
                        id = c.Key, name = MotionCreations.NameOf(c), seconds = MotionCreations.SecondsOf(c), bytes = c.Bytes,
                        fixture = metadata?.Fixture, starter = metadata?.Starter, model = metadata?.Model, where = metadata?.Where,
                        madeFor = metadata?.MadeFor is null ? null : "set", createdAt = c.CreatedAt,
                        completeHere = CreationStore.IsComplete(creations, c), motionCharacters = c.Text?.Length
                    };
                })
            },
            vrm = modelPath is null ? null : VrmView(dataDirectory, creations, kept, modelPath)
        };
    }

    // The kept motions as a VRM model's emotes and motions: their sources, the tags they have and which replies are offered.
    private static object VrmView(string dataDirectory, string creations, IReadOnlyList<Creation> kept, string modelPath)
    {
        if (!Path.IsPathFullyQualified(modelPath) || !File.Exists(modelPath)) throw new ArgumentException("model must be an absolute path to a .vrm file.");
        var inventory = CharacterActionInventory.ReadAsync(AvatarRenderer.Vrm, modelPath, CancellationToken.None).GetAwaiter().GetResult()
            .WithKeptMotions([.. kept.Where(c => CreationStore.IsComplete(creations, c)).Select(Kept)]);
        var catalog = new CharacterActionCatalog(inventory, CharacterActions.Merge(inventory, CharacterActions.Load(dataDirectory, inventory.ModelId)));
        var offered = catalog.Offered(null).Select(e => e.Source.Id).ToHashSet(StringComparer.Ordinal);
        return new
        {
            modelId = inventory.ModelId[..16],
            motions = catalog.Entries.Where(e => CharacterActionInventory.IsKeptMotion(e.Source.Id)).Select(e => new
            {
                id = e.Source.Id, name = e.Source.Name, tag = e.Action.Tag, on = e.Action.Enabled, offered = offered.Contains(e.Source.Id),
                hint = CharacterActions.Hint(e.Source, e.Action)
            })
        };
    }

    /// <summary>Makes one motion (<paramref name="starters"/>: Martlet's starter motions) with the FIXTURE - NOT AI maker and
    /// checks it the way Martlet keeps and plays it, in <paramref name="dataDirectory"/> (a disposable one when null).</summary>
    internal static async Task<object> RunAsync(string? name, string? motion, double? seconds, bool starters, string? dataDirectory,
        string? modelPath, CancellationToken cancellation)
    {
        var steps = new List<object>();
        var ok = true;
        void Step(string step, bool passed, string detail)
        {
            ok &= passed;
            steps.Add(new { step, ok = passed, detail });
        }
        var root = dataDirectory ?? Path.Combine(Path.GetTempPath(), "martlet-motions-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            MotionTools.Kind.Validate();
            Step("body_motion is one fixed tool and a quiet job kind (the request start stays the same)",
                ReferenceEquals(MotionTools.Definition, MotionTools.Definition) && MotionTools.Kind.Quiet && MotionTools.Kind.MaxActive == 1,
                $"{MotionTools.Definition.Name} ({MotionTools.Definition.Utf8Bytes} bytes); job kind {MotionTools.Kind.Name}, one at a time, " +
                $"{MotionTools.Kind.MaxPerHour} an hour, quiet");
            var registry = new CreationRegistry();
            registry.Register(FixtureCreations.Kind);
            var before = CreationTools.Definitions(registry.ToolKinds);
            registry.Register(MotionCreations.Kind);
            var after = CreationTools.Definitions(registry.ToolKinds);
            Step("Keeping motions changes none of the creation tools' words", before.Zip(after).All(p => p.First.Description == p.Second.Description),
                $"creation kinds: {registry.Kinds.Count}, offered to the creation tools: {registry.ToolKinds.Count}");

            IReadOnlyList<MotionArguments> wanted;
            if (starters) wanted = MotionTools.Starters;
            else
            {
                var json = new JsonObject { ["name"] = name ?? "cheer", ["motion"] = motion ?? "A person raises both fists above the head and jumps once." };
                if (seconds is { } length) json["seconds"] = length;
                var (arguments, problem) = MotionTools.Parse(json.ToJsonString());
                if (arguments?.Motion is null) throw new ArgumentException(problem ?? "motion must describe what the body does.");
                wanted = [arguments];
            }
            var creations = CreationsCheck.Folder(root);
            Directory.CreateDirectory(creations);
            var maker = new FixtureMotionMaker(TimeSpan.FromMilliseconds(50));
            var made = new List<object>();
            var keptMotions = new List<KeptMotion>();
            foreach (var arguments in wanted)
            {
                var availability = await maker.GetAvailabilityAsync(cancellation);
                var result = await maker.GenerateAsync(new MotionRequest { Prompt = arguments.Motion!, Seconds = arguments.Seconds }, cancellation);
                var summary = VrmAnimationClip.Inspect(result.Vrma);
                Step($"{arguments.Name}: the FIXTURE maker made a VRMA the VRM renderer takes",
                    availability.Available && result.Fixture && result.Vrma.Length <= VrmAnimationClip.MaximumBytes && summary.Duration > 0,
                    $"{result.Vrma.Length} bytes, {summary.Duration:0.##} s, {summary.Bones.Count} bones{(summary.MovesHips ? ", hips" : "")}, FIXTURE - NOT AI");
                var motionMade = new MotionMade(result.Vrma, result.Seconds > 0 ? result.Seconds : arguments.Seconds, result.Seed, result.Model,
                    result.Where, result.Fixture, result.Took);
                var creation = await CreationStore.AddAsync(creations, MotionCreations.Draft(arguments, motionMade,
                    new CreationAuthor { Device = "mcp-motions-check", Computer = Environment.MachineName }, starter: starters),
                    registry, DateTimeOffset.UtcNow, cancellation);
                var (clip, problem) = await MotionCreations.LoadAsync(creation, CreationStore.Assets(creations, creation), cancellation);
                Step($"{arguments.Name}: kept as a motion creation and read back", clip is not null && clip.AsSpan().SequenceEqual(result.Vrma),
                    $"{creation.Key} ({creation.Bytes} bytes){(problem is null ? "" : ": " + problem)}");
                keptMotions.Add(new(creation.Key, arguments.Name, arguments.Motion!, MotionCreations.SecondsOf(creation)));
                made.Add(new
                {
                    id = creation.Key, name = arguments.Name, seconds = MotionCreations.SecondsOf(creation), bytes = result.Vrma.Length,
                    sha256 = Convert.ToHexStringLower(SHA256.HashData(result.Vrma)), clipName = "kept-" + creation.Key,
                    ready = MotionTools.Ready(creation.Key, arguments.Name, motionMade)
                });
            }
            Step("The creation tools never list or perform a motion", !CreationTools.List(CreationStore.View(creations), registry, "{}", _ => true)
                .Output.Contains(keptMotions[0].Key, StringComparison.Ordinal), "list_creations leaves motions out");

            var inventory = (modelPath is null ? FixtureVrm() : await CharacterActionInventory.ReadAsync(AvatarRenderer.Vrm, modelPath, cancellation))
                .WithKeptMotions(keptMotions);
            var catalog = new CharacterActionCatalog(inventory, CharacterActions.Merge(inventory, null));
            var prompt = catalog.Prompt(null, null);
            var tags = keptMotions.Select(m => catalog.Entries.First(e => e.Source.Id == CharacterActionInventory.KeptMotionPrefix + m.Key).Action.Tag).ToArray();
            Step("Every kept motion is the VRM's motion with a reply tag", prompt is not null && tags.All(t => t is not null && prompt.Tags.Contains("{" + t + "}")),
                $"{(modelPath is null ? "a FIXTURE VRM" : "the VRM model")}: {string.Join(" ", tags.Select(t => "{" + t + "}"))}");
            return new { ok, fixture = true, dataDirectory = dataDirectory is null ? "disposable" : "given", steps, made };
        }
        finally
        {
            if (dataDirectory is null && Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static KeptMotion Kept(Creation creation) =>
        new(creation.Key, MotionCreations.NameOf(creation), creation.Text ?? creation.Summary ?? "", MotionCreations.SecondsOf(creation));

    // A VRM 1.0 with the humanoid bones Martlet's gestures and a VRMA's hips use, for listing kept motions without a model file.
    private static CharacterActionInventory FixtureVrm()
    {
        var bones = string.Join(",", new[] { "hips", "spine", "chest", "neck", "head", "leftUpperArm", "rightUpperArm", "leftLowerArm", "rightLowerArm" }
            .Select((b, i) => $"\"{b}\":{{\"node\":{i}}}"));
        var json = System.Text.Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"2.0\"},\"extensions\":{\"VRMC_vrm\":{\"specVersion\":\"1.0\"," +
            "\"humanoid\":{\"humanBones\":{" + bones + "}}}}}");
        var padded = (json.Length + 3) / 4 * 4;
        var glb = new byte[20 + padded];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(glb, 0x46546C67);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4), 2);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8), (uint)glb.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(12), (uint)padded);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(16), 0x4E4F534A);
        json.CopyTo(glb, 20);
        glb.AsSpan(20 + json.Length).Fill((byte)' ');
        return CharacterActionInventory.From(AvatarRenderer.Vrm, "fixture.vrm", [new("fixture.vrm", glb, "model/gltf-binary")]);
    }
}
