using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Martlet.Conversation;
using Martlet.Core.Cluster;
using Martlet.Core.Creations;
using Martlet.Core.Pictures;
using Martlet.Providers.Pictures;

namespace Martlet.Mcp;

/// <summary>pictures_status and pictures_check (docs/PICTURES.md). The status reads a data directory's Companion › Pictures
/// choice (pictures.json; never a key, only whether one is saved), its Pictures list (pools-local.json, or the list the desktop
/// would make from pictures.json) and the places a picture tries now, its workflow file and the picture creations, plus the
/// draw_picture tool and job kind the conversation offers. The check draws one picture through the production picture maker:
/// the FIXTURE - NOT AI maker, or a ComfyUI at an address (<see cref="ComfyHttpApi"/>, <see cref="ComfyPictureMaker"/> and
/// <see cref="ComfyWorkflows"/>), then keeps it as a picture creation in a data directory and reads it back the way the talk
/// window shows it; or (place pool) rehearses the Pictures list's routing on simulated ComfyUI computers
/// (<see cref="PicturePoolCheck"/>). Cloud providers are never called here (a picture costs money).</summary>
internal static class PicturesCheck
{
    internal static object Status(string dataDirectory)
    {
        var (settings, state) = PicturesSettings.Read(dataDirectory);
        var workflow = PicturesSettings.LoadWorkflow(dataDirectory);
        var pictures = CreationStore.View(dataDirectory).Live.Where(c => c.Kind == PictureCreations.KindName).ToArray();
        return new
        {
            settings = new
            {
                file = state, place = settings.Place.ToString(), on = settings.On, describe = settings.Describe(), hostId = settings.HostId,
                address = settings.Address, workflow = settings.Workflow.ToString(), checkpoint = settings.Checkpoint,
                model = settings.Cloud ? settings.Model : null, ownKeySaved = settings.CredentialId is not null,
                customWorkflowNodes = workflow?.Count
            },
            pool = Pool(dataDirectory, settings),
            kind = new
            {
                name = PictureTools.Kind.Name, maxActive = PictureTools.Kind.MaxActive, perHour = PictureTools.Kind.MaxPerHour,
                timeLimitMinutes = PictureTools.Kind.TimeLimit?.TotalMinutes, doing = PictureTools.Kind.Doing
            },
            tool = new
            {
                name = PictureTools.Definition.Name, description = PictureTools.Definition.Description,
                parameters = JsonNode.Parse(PictureTools.Definition.ParametersJson)
            },
            afterReply = SongsCheck.CreationsAfterReply(singing: false),
            creations = new
            {
                count = pictures.Length,
                pictures = pictures.Take(50).Select(picture =>
                {
                    var metadata = PictureCreations.Metadata(picture);
                    return new
                    {
                        id = picture.Key, shape = metadata?.Shape, width = metadata?.Width, height = metadata?.Height, engine = metadata?.Engine,
                        model = metadata?.Model, where = metadata?.Where, seconds = metadata?.Seconds, fixture = metadata?.Fixture,
                        titleCharacters = picture.Title?.Length, descriptionCharacters = picture.Text?.Length,
                        assets = picture.Assets?.Select(asset => new { name = asset.Name, mediaType = asset.MediaType, bytes = asset.Bytes }).ToArray(),
                        here = CreationStore.IsComplete(dataDirectory, picture), createdBy = picture.CreatedBy?.Computer, createdAt = picture.CreatedAt
                    };
                }).ToArray()
            }
        };
    }

    // This PC's Pictures list (pools-local.json) and the places a picture tries now (PoolRouting.Order, as the desktop: on, kept
    // for this PC, agreed to and, for a paired computer, still paired and not a host a friend shares). Without a list yet, the one
    // the desktop makes once from pictures.json (the chosen place, then the other computers the shared plan says run the
    // pictures role). Read-only: it never saves the list.
    private static object Pool(string dataDirectory, PicturesSettings settings)
    {
        var area = PoolAreas.Pictures;
        var device = Martlet.Diagnostics.LocalLogs.ThisDeviceId();
        var (paired, own) = PairedHosts(dataDirectory);
        var saved = PoolSettings.LoadFor(dataDirectory, area);
        var list = saved ?? MigrationPreview(dataDirectory, settings, paired, own, device);
        var order = PoolRouting.Order(area, list, device, m => m.Kind != PoolMemberKind.Computer || paired.Contains(m.HostId!));
        var keys = PoolKeys.Load(dataDirectory);
        return new
        {
            area = area.Id, lane = PicturePool.Lane, file = PoolSettings.File(area.Shared), configured = saved is not null, off = order.Off,
            device, ownHost = own,
            members = list.Members.Select(m => new
            {
                key = m.Key, kind = m.Kind.ToString(), name = m.Name, off = m.Off, onlyFor = m.OnlyFor, settings = m.Settings,
                consented = m.Consented(area.Id), ownKeySaved = m.Kind == PoolMemberKind.Cloud ? keys.For(area.Id, m.Key) is not null : (bool?)null,
                place = PicturePoolMembers.Place(m, own, keys.For(area.Id, m.Key))?.Describe()
            }),
            tries = order.Members.Select(m => m.Key), pooled = order.Members.Count > 1
        };
    }

    private static PoolList MigrationPreview(string dataDirectory, PicturesSettings settings, string[] paired, string? own, string device)
    {
        ClusterPlan plan;
        try { plan = ClusterPlan.Parse(File.ReadAllBytes(Path.Combine(dataDirectory, "cluster.json"))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException) { plan = ClusterPlan.Empty; }
        var others = settings.Place == PicturePlace.Host
            ? PicturePoolMembers.Others(plan, paired, own, settings.HostId, WorkSharingSettings.Load(dataDirectory), device) : [];
        return PicturePoolMembers.Migrate(settings, others, DateTimeOffset.UtcNow);
    }
    // The paired host IDs in hosts.json other than hosts a friend shares, and the one saved as this PC's own (Docker Desktop here).
    private static (string[] Hosts, string? Own) PairedHosts(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, "hosts.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 262_144) return ([], null);
            var hosts = (JsonNode.Parse(File.ReadAllText(path))?["hosts"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(h => h["access"]?.GetValue<string>() != Martlet.Avatar.Audio2Face.Remote.HostSignInAccess.Friend).ToArray();
            return ([.. hosts.Select(h => h["pairing"]?["hostId"]?.GetValue<string>()).OfType<string>()],
                hosts.FirstOrDefault(h => h["method"]?.GetValue<string>() == "ThisPcDocker")?["pairing"]?["hostId"]?.GetValue<string>());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return ([], null);
        }
    }

    /// <summary>Draws one picture with <paramref name="place"/> (fixture, or comfyui at <paramref name="address"/>), reporting every
    /// progress stage; with <paramref name="dataDirectory"/> keeps it as a picture creation there and reads it back; with
    /// <paramref name="saveDirectory"/> writes the picture there.</summary>
    internal static async Task<object> RunAsync(string? place, string? address, string? workflowName, string? checkpoint, string? workflowFile,
        string? prompt, string? shapeName, string? dataDirectory, string? saveDirectory, CancellationToken cancellation)
    {
        place = (place ?? "fixture").ToLowerInvariant();
        if (place is "openrouter" or "nvidia" or "nvidia-build")
            throw new ArgumentException("pictures_check never calls a paid cloud provider. Use the desktop's Draw a test picture, which asks first.");
        if (place == "pool") return await PicturePoolCheck.RunAsync(cancellation);
        var workflow = (workflowName ?? "z-image-turbo").ToLowerInvariant() switch
        {
            "z-image-turbo" or "zimageturbo" => PictureWorkflow.ZImageTurbo,
            "checkpoint" => PictureWorkflow.Checkpoint,
            "custom" => PictureWorkflow.Custom,
            _ => throw new ArgumentException("workflow is z-image-turbo, checkpoint or custom.")
        };
        JsonObject? custom = null;
        if (workflow == PictureWorkflow.Custom)
        {
            if (workflowFile is null || !Path.IsPathFullyQualified(workflowFile) || !File.Exists(workflowFile))
                throw new ArgumentException("workflowFile must be an absolute path to a ComfyUI Export (API) JSON file.");
            custom = JsonNode.Parse(File.ReadAllBytes(workflowFile)) as JsonObject ?? throw new ArgumentException("workflowFile isn't a JSON object.");
        }
        var shape = PictureShapes.Parse(shapeName) ?? PictureShape.Square;
        var request = new PictureRequest { Prompt = prompt ?? "A small songbird with a bright red breast on a mossy branch at sunrise, soft watercolour", Shape = shape };
        IPictureMaker maker = place switch
        {
            "fixture" => new FixturePictureMaker(TimeSpan.FromMilliseconds(50)),
            "comfyui" => new ComfyPictureMaker(new ComfyHttpApi(address ?? throw new ArgumentException("comfyui needs address, like http://127.0.0.1:8188.")),
                workflow, checkpoint, custom),
            _ => throw new ArgumentException("place is fixture, comfyui or pool.")
        };
        var stages = new List<object>();
        var clock = Stopwatch.StartNew();
        try
        {
            var availability = await maker.GetAvailabilityAsync(cancellation);
            if (!availability.Available)
                return new { ok = false, place, where = maker.Where, available = false, reason = availability.Reason };
            JsonObject? built = null;
            PictureResult result;
            try
            {
                if (place == "comfyui") built = ComfyWorkflows.Build(workflow, request, 1, checkpoint, custom);
                result = await maker.GenerateAsync(request, new SyncProgress(p => stages.Add(new
                {
                    stage = p.Stage, describe = p.Describe(), queuePosition = p.QueuePosition, atMs = clock.ElapsedMilliseconds
                })), cancellation);
            }
            catch (PictureException error)
            {
                return new { ok = false, place, where = maker.Where, code = error.Code, error = error.Message, stages };
            }
            var probe = PictureImages.Probe(result.Image);
            string? saved = null;
            if (saveDirectory is not null)
            {
                if (!Path.IsPathFullyQualified(saveDirectory)) throw new ArgumentException("saveDirectory must be an absolute path.");
                Directory.CreateDirectory(saveDirectory);
                saved = Path.Combine(saveDirectory, "picture" + (result.MediaType == PictureImages.Jpeg ? ".jpg" : result.MediaType == PictureImages.Webp ? ".webp" : ".png"));
                await File.WriteAllBytesAsync(saved, result.Image, cancellation);
            }
            object? kept = null;
            if (dataDirectory is not null)
            {
                var registry = new CreationRegistry();
                registry.Register(PictureCreations.Kind);
                var arguments = PictureTools.Parse($$"""{"description": {{System.Text.Json.JsonSerializer.Serialize(request.Prompt)}}, "title": "Test picture", "shape": "{{PictureShapes.Name(shape)}}"}""").Arguments!;
                var creation = await CreationStore.AddAsync(dataDirectory, PictureCreations.Draft(result, arguments.Title, arguments.About, request,
                    new CreationAuthor { Device = "mcp-pictures-check", Computer = Environment.MachineName }), registry, DateTimeOffset.UtcNow, cancellation);
                var (image, mediaType, problem) = await PictureCreations.LoadAsync(creation, CreationStore.Assets(dataDirectory, creation), cancellation);
                kept = new
                {
                    id = creation.Key, kind = creation.Kind, bytes = creation.Bytes, readBack = image is not null && image.AsSpan().SequenceEqual(result.Image),
                    mediaType, problem, describe = PictureCreations.Kind.DescribeFor(creation), ready = PictureTools.Ready(creation.Key, "Test picture", result)
                };
            }
            return new
            {
                ok = probe is not null, place, where = result.Where, engine = result.Engine, model = result.Model, fixture = result.Fixture,
                mediaType = result.MediaType, width = result.Width, height = result.Height, bytes = result.Image.Length,
                sha256 = Convert.ToHexStringLower(SHA256.HashData(result.Image)), seed = result.Seed, seconds = Math.Round(result.Took.TotalSeconds, 2),
                requested = new { shape = PictureShapes.Name(shape), width = request.Width, height = request.Height },
                workflowNodes = built?.Select(n => n.Value?["class_type"]?.ToString()).ToArray(), stages, saved, kept
            };
        }
        finally { (maker as IDisposable)?.Dispose(); }
    }

    private sealed class SyncProgress(Action<PictureProgress> report) : IProgress<PictureProgress>
    {
        public void Report(PictureProgress value) => report(value);
    }
}
