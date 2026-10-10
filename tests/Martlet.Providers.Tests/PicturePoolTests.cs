using System.Text.Json.Nodes;
using Martlet.Core.Cluster;
using Martlet.Core.Pictures;
using Martlet.Providers.Pictures;

namespace Martlet.Providers.Tests;

public sealed class PicturePoolTests
{
    private static readonly PictureRequest Request = new() { Prompt = "a fox in the snow" };

    [Fact]
    public async Task The_first_free_member_draws_and_the_others_get_no_request()
    {
        var (a, b) = (new Comfy("a"), new Comfy("b"));
        using var pool = Pool([a, b]);
        Assert.True((await pool.GetAvailabilityAsync(CancellationToken.None)).Available);
        var result = await pool.GenerateAsync(Request, null, CancellationToken.None);
        Assert.Equal("a ComfyUI", result.Where);
        Assert.Equal(new PicturePoolRoute("host:a", 0, 0, 0, null), pool.Route);
        Assert.Equal(0, b.Requests);
    }

    [Fact]
    public async Task A_member_drawing_another_picture_is_passed_over_for_the_next_free_one()
    {
        var (a, b) = (new Comfy("a") { Others = 1 }, new Comfy("b"));
        using var pool = Pool([a, b]);
        List<string> placed = [];
        pool.Placed += member => placed.Add(member.Id);
        var result = await pool.GenerateAsync(Request, null, CancellationToken.None);
        Assert.Equal("b ComfyUI", result.Where);
        Assert.Equal(new PicturePoolRoute("host:b", 1, 1, 0, null), pool.Route);
        Assert.Equal(["host:b"], placed);
        Assert.Equal(0, a.Prompts);
        Assert.Equal("b", pool.Chosen!.HostId);
    }

    [Fact]
    public async Task Members_that_are_down_lack_the_checkpoint_or_reject_the_workflow_are_passed_over()
    {
        var (down, lacking, rejecting, good) = (new Comfy("down") { Down = true }, new Comfy("lacking"),
            new Comfy("rejecting") { Checkpoints = ["c.safetensors"], Rejects = true }, new Comfy("good") { Checkpoints = ["c.safetensors"] });
        using var pool = Pool([down, lacking, rejecting, good], PictureWorkflow.Checkpoint, "c.safetensors");
        var result = await pool.GenerateAsync(Request, null, CancellationToken.None);
        Assert.Equal("good ComfyUI", result.Where);
        Assert.Equal(new PicturePoolRoute("host:good", 3, 0, 3, null), pool.Route);
        Assert.Equal(0, lacking.Prompts);
        Assert.Equal(0, rejecting.Drawn);
    }

    [Fact]
    public async Task Every_member_busy_waits_in_the_shortest_comfy_queue_asking_each_once()
    {
        var (a, b, c) = (new Comfy("a") { Others = 3 }, new Comfy("b") { Others = 1 }, new Comfy("c") { Others = 1 });
        using var pool = Pool([a, b, c]);
        var result = await pool.GenerateAsync(Request, null, CancellationToken.None);
        Assert.Equal("b ComfyUI", result.Where);
        Assert.Equal(1, pool.Route!.QueuedBehind);
        Assert.Equal(1, a.QueueChecks);
        Assert.Equal(1, c.QueueChecks);
    }

    [Fact]
    public async Task One_member_draws_as_it_does_alone_without_a_queue_check()
    {
        var a = new Comfy("a") { Others = 2 };
        using var pool = Pool([a]);
        await pool.GenerateAsync(Request, null, CancellationToken.None);
        Assert.Equal(0, a.QueueChecksBeforePrompt);
        Assert.Equal(1, a.Drawn);
    }

    [Fact]
    public async Task Two_pictures_at_once_go_to_two_members()
    {
        var (a, b) = (new Comfy("a"), new Comfy("b"));
        var queue = new WorkQueue();
        using var one = Pool([a, b], queue: queue);
        using var two = Pool([a, b], queue: queue);
        await Task.WhenAll(one.GenerateAsync(Request, null, CancellationToken.None), two.GenerateAsync(Request, null, CancellationToken.None));
        Assert.Equal((1, 1), (a.Drawn, b.Drawn));
    }

    [Fact]
    public async Task A_busy_or_refusing_cloud_member_passes_the_picture_on()
    {
        var b = new Comfy("b");
        using var pool = new PicturePool([
            new("cloud:openrouter/x", new Failing(PictureErrorCodes.Busy)),
            new("cloud:nvidia-build/y", new Failing(PictureErrorCodes.NotAuthorized)),
            new("host:b", Maker(b), "b")], new WorkQueue());
        var result = await pool.GenerateAsync(Request, null, CancellationToken.None);
        Assert.Equal("b ComfyUI", result.Where);
        Assert.Equal(new PicturePoolRoute("host:b", 2, 1, 1, null), pool.Route);
    }

    [Fact]
    public async Task No_member_can_draw_it_fails_with_the_first_reason_and_a_content_refusal_is_not_passed_on()
    {
        using var down = Pool([new Comfy("a") { Down = true }, new Comfy("b") { Down = true }]);
        var error = await Assert.ThrowsAsync<PictureException>(() => down.GenerateAsync(Request, null, CancellationToken.None));
        Assert.Equal(PictureErrorCodes.Unavailable, error.Code);
        Assert.StartsWith("a isn't reachable", error.Message);
        Assert.False((await down.GetAvailabilityAsync(CancellationToken.None)).Available);

        var b = new Comfy("b");
        using var refused = new PicturePool([new("cloud:openrouter/x", new Failing(PictureErrorCodes.Refused)), new("host:b", Maker(b), "b")], new WorkQueue());
        Assert.Equal(PictureErrorCodes.Refused,
            (await Assert.ThrowsAsync<PictureException>(() => refused.GenerateAsync(Request, null, CancellationToken.None))).Code);
        Assert.Equal(0, b.Requests);
    }

    private static PicturePool Pool(IReadOnlyList<Comfy> computers, PictureWorkflow workflow = PictureWorkflow.ZImageTurbo, string? checkpoint = null,
        WorkQueue? queue = null) =>
        new([.. computers.Select(c => new PicturePoolMember("host:" + c.Id, Maker(c, workflow, checkpoint), c.Id))], queue ?? new WorkQueue());

    private static ComfyPictureMaker Maker(Comfy computer, PictureWorkflow workflow = PictureWorkflow.ZImageTurbo, string? checkpoint = null) =>
        new(computer, workflow, checkpoint, poll: TimeSpan.FromMilliseconds(2));

    private sealed class Failing(string code) : IPictureMaker
    {
        public string Where => "a cloud provider";
        public Task<PictureMakerAvailability> GetAvailabilityAsync(CancellationToken cancellationToken) => Task.FromResult(new PictureMakerAvailability(true, null, Where));
        public Task<PictureResult> GenerateAsync(PictureRequest request, IProgress<PictureProgress>? progress, CancellationToken cancellationToken) =>
            throw new PictureException(code, $"{Where} said {code}.");
    }

    /// <summary>A ComfyUI with <see cref="Others"/> pictures of other companion PCs in its queue; each picture is drawn after three
    /// history polls.</summary>
    private sealed class Comfy(string id) : IComfyApi
    {
        private readonly object gate = new();
        private readonly Dictionary<string, int> mine = new(StringComparer.Ordinal);
        private int requests;
        public string Id { get; } = id;
        public string Where => $"{Id} ComfyUI";
        public bool Down { get; init; }
        public bool Rejects { get; init; }
        public int Others { get; init; }
        public IReadOnlyList<string> Checkpoints { get; init; } = [];
        public int Requests => Volatile.Read(ref requests);
        public int Prompts { get; private set; }
        public int Drawn { get; private set; }
        public int QueueChecks { get; private set; }
        public int QueueChecksBeforePrompt { get; private set; }

        private void Ask()
        {
            Interlocked.Increment(ref requests);
            if (Down) throw new PictureException(PictureErrorCodes.Unavailable, $"{Id} isn't reachable.");
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
            if (Rejects) throw new PictureException(PictureErrorCodes.RequestInvalid, $"{Where} rejected the workflow.");
            lock (gate)
            {
                var prompt = $"{Id}-{++Prompts}";
                mine[prompt] = 0;
                return Task.FromResult(prompt);
            }
        }

        public Task<JsonObject?> HistoryAsync(string promptId, CancellationToken cancellationToken)
        {
            Ask();
            lock (gate)
            {
                if (!mine.TryGetValue(promptId, out var polls) || ++polls < 3)
                {
                    if (mine.ContainsKey(promptId)) mine[promptId] = polls;
                    return Task.FromResult<JsonObject?>(null);
                }
                mine.Remove(promptId);
                Drawn++;
            }
            return Task.FromResult<JsonObject?>(JsonNode.Parse(
                """{"status":{"status_str":"success","completed":true},"outputs":{"9":{"images":[{"filename":"m.png","subfolder":"","type":"output"}]}}}""")!.AsObject());
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
                    ["queue_running"] = new JsonArray([.. all.Take(1).Select((p, i) => (JsonNode?)new JsonArray(i, p))]),
                    ["queue_pending"] = new JsonArray([.. all.Skip(1).Select((p, i) => (JsonNode?)new JsonArray(i + 1, p))])
                });
            }
        }

        public Task<byte[]> ViewAsync(string filename, string subfolder, string type, CancellationToken cancellationToken)
        {
            Ask();
            return Task.FromResult(FixturePictureMaker.Png(16, 16, new byte[32]));
        }

        public Task CancelAsync(string promptId, CancellationToken cancellationToken)
        {
            lock (gate) mine.Remove(promptId);
            return Task.CompletedTask;
        }

        public Task FreeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
