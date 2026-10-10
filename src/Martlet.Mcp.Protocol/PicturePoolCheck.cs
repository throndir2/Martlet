using System.Text.Json.Nodes;
using Martlet.Core.Cluster;
using Martlet.Core.Pictures;
using Martlet.Providers.Pictures;

namespace Martlet.Mcp;

/// <summary>pictures_check with place "pool": rehearses the production picture pool (<see cref="PicturePool"/>, with
/// <see cref="WorkQueue"/> and <see cref="ComfyPictureMaker"/>) on simulated ComfyUI computers that keep a queue as ComfyUI does,
/// so a computer drawing another companion PC's picture shows it in its queue. FIXTURE - NOT real hosts or models; nothing
/// leaves the process.</summary>
internal static class PicturePoolCheck
{
    private const string Checkpoint = "dreamshaper_8.safetensors";

    internal static async Task<object> RunAsync(CancellationToken cancellation)
    {
        List<object> steps = [];
        var ok = true;
        void Step(string name, bool passed, object detail)
        {
            ok &= passed;
            steps.Add(new { name, passed, detail });
        }
        var request = new PictureRequest { Prompt = "A small songbird on a mossy branch at sunrise, soft watercolour" };

        var (a, b) = (new Computer("m4-host"), new Computer("m1-host"));
        var (result, route) = await DrawAsync([a, b], request, PictureWorkflow.ZImageTurbo, cancellation);
        Step("The chosen computer is free: it draws, and the others get no request",
            route?.Id == "host:m4-host" && a.Drawn == 1 && b.Requests == 0, new { where = result?.Where, route, otherRequests = b.Requests });

        (a, b) = (new Computer("m4-host") { Others = 1 }, new Computer("m1-host"));
        (result, route) = await DrawAsync([a, b], request, PictureWorkflow.ZImageTurbo, cancellation);
        Step("The chosen computer draws another companion PC's picture: the next free one draws it at once",
            route is { Id: "host:m1-host", Busy: 1, QueuedBehind: null } && b.Drawn == 1 && a.Prompts == 0, new { where = result?.Where, route });

        (a, b) = (new Computer("m4-host") { Down = true }, new Computer("m1-host"));
        (result, route) = await DrawAsync([a, b], request, PictureWorkflow.ZImageTurbo, cancellation);
        Step("The chosen computer doesn't answer: passed over at once", route is { Id: "host:m1-host", Unavailable: 1 } && b.Drawn == 1,
            new { where = result?.Where, route });

        (a, b) = (new Computer("m4-host"), new Computer("m1-host") { Checkpoints = [Checkpoint] });
        (result, route) = await DrawAsync([a, b], request, PictureWorkflow.Checkpoint, cancellation);
        Step("A computer without the chosen checkpoint is passed over for one that has it",
            route?.Id == "host:m1-host" && b.Drawn == 1 && a.Prompts == 0, new { where = result?.Where, route });

        (a, b) = (new Computer("m4-host") { RejectsWorkflow = true }, new Computer("m1-host"));
        (result, route) = await DrawAsync([a, b], request, PictureWorkflow.Custom, cancellation);
        Step("A computer that rejects the custom workflow (a custom node it doesn't have) is passed over",
            route?.Id == "host:m1-host" && b.Drawn == 1 && a.Drawn == 0, new { where = result?.Where, route });

        (a, b) = (new Computer("m4-host") { Others = 3 }, new Computer("m1-host") { Others = 1 });
        (result, route) = await DrawAsync([a, b], request, PictureWorkflow.ZImageTurbo, cancellation);
        Step("Every computer is busy: the picture waits in the shortest ComfyUI queue, asking each once",
            route is { Id: "host:m1-host", QueuedBehind: 1 } && b.Drawn == 1 && a.QueueChecks == 1, new { where = result?.Where, route, firstChecks = a.QueueChecks });

        (a, b) = (new Computer("m4-host"), new Computer("m1-host"));
        var queue = new WorkQueue();
        using (var one = Pool([a, b], PictureWorkflow.ZImageTurbo, queue))
        using (var two = Pool([a, b], PictureWorkflow.ZImageTurbo, queue))
        {
            var both = await Task.WhenAll(one.GenerateAsync(request, null, cancellation), two.GenerateAsync(request, null, cancellation));
            Step("Two pictures at once from this PC are drawn on two computers", a.Drawn == 1 && b.Drawn == 1,
                new { first = both[0].Where, second = both[1].Where });
        }

        var single = new Computer("m4-host") { Others = 2 };
        (result, route) = await DrawAsync([single], request, PictureWorkflow.ZImageTurbo, cancellation);
        Step("One picture computer: it draws there as before, with no extra request", single.Drawn == 1 && single.QueueChecksBeforePrompt == 0,
            new { where = result?.Where, route });

        (a, b) = (new Computer("m4-host") { Down = true }, new Computer("m1-host") { Down = true });
        string? problem = null;
        try { await DrawAsync([a, b], request, PictureWorkflow.ZImageTurbo, cancellation, check: false); }
        catch (PictureException error) { problem = $"{error.Code}: {error.Message}"; }
        Step("No computer answers: the picture fails with the reason", problem?.StartsWith(PictureErrorCodes.Unavailable, StringComparison.Ordinal) == true,
            new { problem });

        var older = new PicturesSettings { Place = PicturePlace.Host, HostId = "m4-host", Workflow = PictureWorkflow.Checkpoint, Checkpoint = Checkpoint };
        var plan = ClusterPlan.Empty with
        {
            Nodes = [Node("m1-host", "pictures"), Node("m3-host", "pictures"), Node("m4-host", "pictures"), Node("m2-host", "stt")]
        };
        var others = PicturePoolMembers.Others(plan, ["m1-host", "m2-host", "m3-host", "m4-host"], "m3-host", "m4-host",
            new WorkSharingSettings().With(new WorkSharingHost { HostId = "m1-host", OnlyFor = ["desk-9"] }), "desk-1");
        var migrated = PicturePoolMembers.Migrate(older, others, DateTimeOffset.UtcNow);
        Step("pictures.json becomes the Pictures list once: the chosen computer, then this PC's own, then the others that draw, " +
             "each with the same workflow; one kept for another companion PC is left out",
            migrated.Members.Select(m => m.Key).SequenceEqual(["host:m4-host", "host:m3-host"]) &&
            migrated.Members.All(m => m.Setting(PoolSettingKeys.Checkpoint) == Checkpoint),
            new { members = migrated.Members.Select(m => new { m.Key, m.Settings }) });

        var cloud = PicturePoolMembers.Migrate(new PicturesSettings { Place = PicturePlace.OpenRouter }, [], DateTimeOffset.UtcNow);
        var unagreed = new PoolList { Area = PoolAreas.Pictures.Id, Members = [PoolMember.Cloud(PicturePoolMembers.NvidiaBuild, "black-forest-labs/flux.1-schnell")] };
        Step("A cloud provider chosen before keeps the owner's agreement; one without an agreement takes no pictures",
            PoolRouting.Order(PoolAreas.Pictures, cloud, "desk-1").Members.Count == 1 && PoolRouting.Order(PoolAreas.Pictures, unagreed, "desk-1").Off == false &&
            PoolRouting.Order(PoolAreas.Pictures, unagreed, "desk-1").Members.Count == 0,
            new { cloud = cloud.Members.Select(m => m.Key), unagreed = unagreed.Members.Select(m => m.Key) });

        var empty = PoolRouting.Order(PoolAreas.Pictures, new PoolList { Area = PoolAreas.Pictures.Id }, "desk-1");
        Step("An empty Pictures list means pictures are off", empty.Off && empty.Members.Count == 0, new { empty.Off });

        return new { ok, fixture = "simulated ComfyUI computers (FIXTURE - NOT real hosts or models)", lane = PicturePool.Lane, steps };
    }

    private static ClusterNode Node(string host, string role) => new()
    {
        HostId = host, Roles = [new ClusterNodeRole { Kind = role, Model = "" }], Revision = 1, UpdatedAt = DateTimeOffset.UnixEpoch, UpdatedBy = "mcp"
    };

    private static PicturePool Pool(IReadOnlyList<Computer> computers, PictureWorkflow workflow, WorkQueue queue) => new(
        [.. computers.Select(c => new PicturePoolMember(PoolMember.Computer(c.Id).Key, new ComfyPictureMaker(c, workflow,
            workflow == PictureWorkflow.Checkpoint ? Checkpoint : null, workflow == PictureWorkflow.Custom ? Custom() : null,
            poll: TimeSpan.FromMilliseconds(5)), c.Id))], queue);

    private static async Task<(PictureResult? Result, PicturePoolRoute? Route)> DrawAsync(IReadOnlyList<Computer> computers, PictureRequest request,
        PictureWorkflow workflow, CancellationToken token, bool check = true)
    {
        using var pool = Pool(computers, workflow, new WorkQueue());
        if (check && await pool.GetAvailabilityAsync(token) is { Available: false } availability)
            throw new PictureException(PictureErrorCodes.Unavailable, availability.Reason ?? "unavailable");
        var result = await pool.GenerateAsync(request, null, token);
        return (result, pool.Route);
    }

    private static JsonObject Custom() => JsonNode.Parse("""
        {"1":{"class_type":"MartletCustomNode","inputs":{"text":"{{prompt}}","seed":"{{seed}}"}},
         "2":{"class_type":"SaveImage","inputs":{"images":["1",0]}}}
        """)!.AsObject();

    /// <summary>A simulated ComfyUI computer: a queue with <see cref="Others"/> pictures of other companion PCs ahead, then this
    /// PC's own, each drawn after three history polls.</summary>
    private sealed class Computer(string id) : IComfyApi
    {
        private readonly object gate = new();
        private readonly Dictionary<string, int> mine = new(StringComparer.Ordinal);
        private int requests;
        public string Id { get; } = id;
        public string Where => $"{Id}'s Pictures role (simulated)";
        public bool Down { get; init; }
        public int Others { get; init; }
        public IReadOnlyList<string> Checkpoints { get; init; } = [];
        public bool RejectsWorkflow { get; init; }
        public int Requests => Volatile.Read(ref requests);
        public int Prompts { get; private set; }
        public int Drawn { get; private set; }
        public int QueueChecks { get; private set; }
        public int QueueChecksBeforePrompt { get; private set; }

        private void Ask()
        {
            Interlocked.Increment(ref requests);
            if (Down) throw new PictureException(PictureErrorCodes.Unavailable, $"{Id} isn't reachable (simulated).");
        }

        public Task<JsonObject> StatusAsync(CancellationToken cancellationToken)
        {
            Ask();
            return Task.FromResult(new JsonObject
            {
                ["state"] = "ready",
                ["models"] = new JsonObject
                {
                    ["diffusion_models"] = new JsonArray(ComfyWorkflows.ZImageModel), ["text_encoders"] = new JsonArray(ComfyWorkflows.ZImageTextEncoder),
                    ["vae"] = new JsonArray(ComfyWorkflows.ZImageVae), ["checkpoints"] = new JsonArray([.. Checkpoints.Select(c => (JsonNode?)c)])
                }
            });
        }

        public Task<string> QueueAsync(JsonObject workflow, CancellationToken cancellationToken)
        {
            Ask();
            if (RejectsWorkflow)
                throw new PictureException(PictureErrorCodes.RequestInvalid, $"{Where} rejected the workflow: Cannot execute because node MartletCustomNode does not exist.");
            lock (gate)
            {
                var prompt = $"{Id}-p{++Prompts}";
                mine[prompt] = 0;
                return Task.FromResult(prompt);
            }
        }

        public Task<JsonObject?> HistoryAsync(string promptId, CancellationToken cancellationToken)
        {
            Ask();
            lock (gate)
            {
                if (!mine.TryGetValue(promptId, out var polls)) return Task.FromResult<JsonObject?>(null);
                if (++polls < 3)
                {
                    mine[promptId] = polls;
                    return Task.FromResult<JsonObject?>(null);
                }
                mine.Remove(promptId);
                Drawn++;
            }
            return Task.FromResult<JsonObject?>(JsonNode.Parse(
                """{"status":{"status_str":"success","completed":true},"outputs":{"9":{"images":[{"filename":"martlet_00001_.png","subfolder":"martlet","type":"output"}]}}}""")!.AsObject());
        }

        public Task<JsonObject> QueueStateAsync(CancellationToken cancellationToken)
        {
            Ask();
            lock (gate)
            {
                QueueChecks++;
                if (Prompts == 0) QueueChecksBeforePrompt++;
                var all = Enumerable.Range(1, Others).Select(i => $"other-{i}").Concat(mine.Keys).ToArray();
                return Task.FromResult(new JsonObject
                {
                    ["queue_running"] = new JsonArray([.. all.Take(1).Select((p, i) => (JsonNode)new JsonArray(i, p))]),
                    ["queue_pending"] = new JsonArray([.. all.Skip(1).Select((p, i) => (JsonNode)new JsonArray(i + 1, p))])
                });
            }
        }

        public Task<byte[]> ViewAsync(string filename, string subfolder, string type, CancellationToken cancellationToken)
        {
            Ask();
            return Task.FromResult(FixturePictureMaker.Png(64, 64, System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Id))));
        }

        public Task CancelAsync(string promptId, CancellationToken cancellationToken)
        {
            lock (gate) mine.Remove(promptId);
            return Task.CompletedTask;
        }

        public Task FreeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
