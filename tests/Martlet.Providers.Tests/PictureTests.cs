using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Martlet.Core.Pictures;
using Martlet.Providers.Pictures;

namespace Martlet.Providers.Tests;

public sealed class PictureTests
{
    private static readonly byte[] Png = FixturePictureMaker.Png(32, 16, new byte[32]);

    [Fact]
    public void Probe_reads_png_and_jpeg_sizes_and_rejects_other_bytes()
    {
        Assert.Equal((PictureImages.Png, 32, 16), PictureImages.Probe(Png));
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x02, 0x00, 0x03, 0x00, 0x03, 0, 0, 0];
        Assert.Equal((PictureImages.Jpeg, 768, 512), PictureImages.Probe(jpeg));
        Assert.Null(PictureImages.Probe("<svg/>"u8));
        Assert.Throws<PictureException>(() => PictureImages.Require("nope"u8.ToArray(), "Test"));
    }

    [Fact]
    public void ZImage_workflow_carries_the_request()
    {
        var request = new PictureRequest { Prompt = "a fox", NegativePrompt = "blur", Shape = PictureShape.Wide };
        var workflow = ComfyWorkflows.Build(PictureWorkflow.ZImageTurbo, request, 42);
        Assert.Equal("a fox", workflow["6"]!["inputs"]!["text"]!.ToString());
        Assert.Equal("blur", workflow["7"]!["inputs"]!["text"]!.ToString());
        Assert.Equal(42, workflow["3"]!["inputs"]!["seed"]!.GetValue<long>());
        Assert.Equal(1344, workflow["13"]!["inputs"]!["width"]!.GetValue<int>());
        Assert.Equal(768, workflow["13"]!["inputs"]!["height"]!.GetValue<int>());
        Assert.Equal(ComfyWorkflows.ZImageModel, workflow["16"]!["inputs"]!["unet_name"]!.ToString());
    }

    [Fact]
    public void Checkpoint_workflow_sizes_sd15_smaller_than_xl()
    {
        var request = new PictureRequest { Prompt = "a fox" };
        Assert.Equal((512, 512), ComfyWorkflows.CheckpointSize(request, "v1-5-pruned.safetensors"));
        Assert.Equal((1024, 1024), ComfyWorkflows.CheckpointSize(request, "sd_xl_base_1.0.safetensors"));
        var workflow = ComfyWorkflows.Build(PictureWorkflow.Checkpoint, request, 7, "dreamshaper_8.safetensors");
        Assert.Equal("dreamshaper_8.safetensors", workflow["4"]!["inputs"]!["ckpt_name"]!.ToString());
        Assert.Throws<PictureException>(() => ComfyWorkflows.Build(PictureWorkflow.Checkpoint, request, 7));
    }

    [Fact]
    public void Custom_workflow_placeholders_are_filled()
    {
        var workflow = JsonNode.Parse("""
            {"1":{"class_type":"CLIPTextEncode","inputs":{"text":"masterpiece, {{prompt}}","clip":["2",1]}},
             "2":{"class_type":"KSampler","inputs":{"seed":"{{seed}}","positive":["1",0]}},
             "3":{"class_type":"EmptyLatentImage","inputs":{"width":"{{width}}","height":"{{height}}"}}}
            """)!.AsObject();
        var filled = ComfyWorkflows.Fill(workflow, new PictureRequest { Prompt = "a fox", Shape = PictureShape.Portrait }, 99);
        Assert.Equal("masterpiece, a fox", filled["1"]!["inputs"]!["text"]!.ToString());
        Assert.Equal(99, filled["2"]!["inputs"]!["seed"]!.GetValue<long>());
        Assert.Equal(832, filled["3"]!["inputs"]!["width"]!.GetValue<int>());
        Assert.Contains("{{prompt}}", workflow["1"]!["inputs"]!["text"]!.ToString());
    }

    [Fact]
    public void Custom_workflow_without_placeholders_fills_the_samplers_prompt_seed_and_size()
    {
        var workflow = JsonNode.Parse("""
            {"6":{"class_type":"CLIPTextEncode","inputs":{"text":"old prompt","clip":["4",1]}},
             "7":{"class_type":"CLIPTextEncode","inputs":{"text":"old negative","clip":["4",1]}},
             "3":{"class_type":"KSampler","inputs":{"seed":5,"positive":["6",0],"negative":["7",0]}},
             "5":{"class_type":"EmptySD3LatentImage","inputs":{"width":512,"height":512,"batch_size":1}}}
            """)!.AsObject();
        var filled = ComfyWorkflows.Fill(workflow, new PictureRequest { Prompt = "a fox", NegativePrompt = "people" }, 11);
        Assert.Equal("a fox", filled["6"]!["inputs"]!["text"]!.ToString());
        Assert.Equal("people", filled["7"]!["inputs"]!["text"]!.ToString());
        Assert.Equal(11, filled["3"]!["inputs"]!["seed"]!.GetValue<long>());
        Assert.Equal(1024, filled["5"]!["inputs"]!["width"]!.GetValue<int>());
    }

    [Fact]
    public void A_ui_format_workflow_is_refused_clearly()
    {
        var ui = JsonNode.Parse("""{"nodes":[],"links":[],"version":0.4}""")!.AsObject();
        var error = Assert.Throws<PictureException>(() => ComfyWorkflows.Fill(ui, new PictureRequest { Prompt = "x" }, 1));
        Assert.Contains("Export (API)", error.Message);
    }

    [Fact]
    public async Task Comfy_maker_queues_follows_and_fetches_the_picture()
    {
        var api = new FakeComfy();
        using var maker = new ComfyPictureMaker(api, PictureWorkflow.ZImageTurbo, poll: TimeSpan.FromMilliseconds(1));
        Assert.True((await maker.GetAvailabilityAsync(CancellationToken.None)).Available);
        var stages = new List<string>();
        var result = await maker.GenerateAsync(new PictureRequest { Prompt = "a fox", Seed = 3 }, new Inline(p => stages.Add(p.Stage)), CancellationToken.None);
        Assert.Equal(PictureImages.Png, result.MediaType);
        Assert.Equal((32, 16), (result.Width, result.Height));
        Assert.Equal(3, result.Seed);
        Assert.Equal("a fox", api.Queued!["6"]!["inputs"]!["text"]!.ToString());
        Assert.Equal(("martlet_00001_.png", "martlet", "output"), api.Viewed);
        Assert.Contains(PictureProgress.Queued, stages);
        Assert.Contains(PictureProgress.Fetching, stages);
    }

    [Fact]
    public async Task Comfy_maker_names_missing_model_files_and_workflow_failures()
    {
        var api = new FakeComfy { Diffusion = [] };
        using var maker = new ComfyPictureMaker(api, PictureWorkflow.ZImageTurbo, poll: TimeSpan.FromMilliseconds(1));
        var availability = await maker.GetAvailabilityAsync(CancellationToken.None);
        Assert.False(availability.Available);
        Assert.Contains(ComfyWorkflows.ZImageModel, availability.Reason);

        api.Fail = true;
        var error = await Assert.ThrowsAsync<PictureException>(() => maker.GenerateAsync(new PictureRequest { Prompt = "a fox" }, null, CancellationToken.None));
        Assert.Contains("out of memory", error.Message);
    }

    [Fact]
    public async Task Comfy_http_api_reads_status_and_reports_validation_errors()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/system_stats" => Json("""{"system":{"comfyui_version":"0.3.99"},"devices":[{"name":"cuda:0"}]}"""),
            "/models/checkpoints" => Json("""["a.safetensors"]"""),
            "/prompt" => Json("""{"error":{"type":"prompt_outputs_failed_validation","message":"Prompt outputs failed validation"},"node_errors":{"4":{"errors":[{"message":"Value not in list","details":"ckpt_name: 'x' not in []"}]}}}""", HttpStatusCode.BadRequest),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        using var api = new ComfyHttpApi("127.0.0.1:8188", handler);
        Assert.Equal("http://127.0.0.1:8188", api.Address);
        var status = await api.StatusAsync(CancellationToken.None);
        Assert.Equal("0.3.99", status["comfyui_version"]!.ToString());
        Assert.Equal("a.safetensors", status["models"]!["checkpoints"]![0]!.ToString());
        var error = await Assert.ThrowsAsync<PictureException>(() => api.QueueAsync(new JsonObject(), CancellationToken.None));
        Assert.Equal(PictureErrorCodes.RequestInvalid, error.Code);
        Assert.Contains("Value not in list", error.Message);
    }

    [Fact]
    public async Task OpenRouter_maker_posts_to_the_image_api_with_the_key()
    {
        HttpRequestMessage? seen = null;
        string? body = null;
        var handler = new Handler(request =>
        {
            seen = request;
            body = request.Content!.ReadAsStringAsync().Result;
            return Json($$"""{"data":[{"b64_json":"{{Convert.ToBase64String(Png)}}","media_type":"image/png"}]}""");
        });
        using var maker = new OpenRouterPictureMaker(null, _ => Task.FromResult<string?>("sk-or-test"), handler);
        var result = await maker.GenerateAsync(new PictureRequest { Prompt = "a fox", Shape = PictureShape.Landscape }, null, CancellationToken.None);
        Assert.Equal("https://openrouter.ai/api/v1/images", seen!.RequestUri!.ToString());
        Assert.Equal("Bearer sk-or-test", seen.Headers.Authorization!.ToString());
        var sent = JsonNode.Parse(body!)!;
        Assert.Equal(PicturesSettings.OpenRouterDefaultModel, sent["model"]!.ToString());
        Assert.Equal("3:2", sent["aspect_ratio"]!.ToString());
        Assert.Equal(OpenRouterPictureMaker.EngineName, result.Engine);
        Assert.Equal(Png, result.Image);
    }

    [Fact]
    public async Task Nvidia_maker_reads_the_artifact_and_reports_the_content_filter()
    {
        var filtered = false;
        var handler = new Handler(request =>
        {
            Assert.Equal("https://ai.api.nvidia.com/v1/genai/black-forest-labs/flux.1-schnell", request.RequestUri!.ToString());
            var sent = JsonNode.Parse(request.Content!.ReadAsStringAsync().Result)!;
            Assert.Equal(4, sent["steps"]!.GetValue<int>());
            return Json(filtered
                ? """{"artifacts":[{"base64":"","finishReason":"CONTENT_FILTERED","seed":1}]}"""
                : $$"""{"artifacts":[{"base64":"{{Convert.ToBase64String(Png)}}","finishReason":"SUCCESS","seed":77}]}""");
        });
        using var maker = new NvidiaPictureMaker(null, _ => Task.FromResult<string?>("nvapi-test"), handler);
        var result = await maker.GenerateAsync(new PictureRequest { Prompt = "a fox" }, null, CancellationToken.None);
        Assert.Equal(77, result.Seed);
        filtered = true;
        var error = await Assert.ThrowsAsync<PictureException>(() => maker.GenerateAsync(new PictureRequest { Prompt = "a fox" }, null, CancellationToken.None));
        Assert.Equal(PictureErrorCodes.Refused, error.Code);
    }

    [Fact]
    public async Task Cloud_maker_without_a_key_is_unavailable_and_sends_nothing()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("sent"));
        using var maker = new OpenRouterPictureMaker(null, _ => Task.FromResult<string?>(null), handler);
        Assert.False((await maker.GetAvailabilityAsync(CancellationToken.None)).Available);
        var error = await Assert.ThrowsAsync<PictureException>(() => maker.GenerateAsync(new PictureRequest { Prompt = "x" }, null, CancellationToken.None));
        Assert.Equal(PictureErrorCodes.NotAuthorized, error.Code);
    }

    [Fact]
    public void Settings_validate_addresses_and_keys()
    {
        Assert.Equal("http://studio:8188", PicturesSettings.ComfyAddress("studio:8188/"));
        Assert.ThrowsAny<Exception>(() => PicturesSettings.ComfyAddress("http://user:pw@studio:8188"));
        new PicturesSettings { Place = PicturePlace.ComfyUi, Address = "http://studio:8188" }.Validate();
        Assert.ThrowsAny<Exception>(() => new PicturesSettings { Place = PicturePlace.Host, CredentialId = Guid.NewGuid() }.Validate());
        var thinking = new Martlet.Core.Settings.SetupRoute
        {
            RouteSchemaVersion = 1, RouteType = Martlet.Core.Settings.SetupRouteType.ChatCompletions, Role = Martlet.Core.Settings.SetupRole.Llm,
            ProviderAlias = "chat-completions", Origin = PicturesSettings.NvidiaBuildOrigin, ModelId = "m", CredentialId = Guid.NewGuid(),
            ConfigurationRevision = Guid.NewGuid()
        };
        Assert.True(new PicturesSettings { Place = PicturePlace.NvidiaBuild }.UsesThinkingKey(thinking));
        Assert.False(new PicturesSettings { Place = PicturePlace.OpenRouter }.UsesThinkingKey(thinking));
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }

    private sealed class Inline(Action<PictureProgress> report) : IProgress<PictureProgress>
    {
        public void Report(PictureProgress value) => report(value);
    }

    private sealed class FakeComfy : IComfyApi
    {
        private int polls;
        public JsonArray Diffusion { get; init; } = [ComfyWorkflows.ZImageModel];
        public bool Fail { get; set; }
        public JsonObject? Queued { get; private set; }
        public (string, string, string)? Viewed { get; private set; }
        public string Where => "fake ComfyUI";

        public Task<JsonObject> StatusAsync(CancellationToken cancellationToken) => Task.FromResult(new JsonObject
        {
            ["state"] = "ready",
            ["models"] = new JsonObject
            {
                ["diffusion_models"] = Diffusion.DeepClone(), ["text_encoders"] = new JsonArray(ComfyWorkflows.ZImageTextEncoder),
                ["vae"] = new JsonArray(ComfyWorkflows.ZImageVae)
            }
        });

        public Task<string> QueueAsync(JsonObject workflow, CancellationToken cancellationToken)
        {
            Queued = workflow;
            return Task.FromResult("p1");
        }

        public Task<JsonObject?> HistoryAsync(string promptId, CancellationToken cancellationToken)
        {
            if (++polls < 3) return Task.FromResult<JsonObject?>(null);
            return Task.FromResult<JsonObject?>(Fail
                ? JsonNode.Parse("""{"status":{"status_str":"error","completed":false,"messages":[["execution_error",{"node_type":"KSampler","exception_message":"CUDA out of memory"}]]},"outputs":{}}""")!.AsObject()
                : JsonNode.Parse("""{"status":{"status_str":"success","completed":true},"outputs":{"9":{"images":[{"filename":"martlet_00001_.png","subfolder":"martlet","type":"output"}]}}}""")!.AsObject());
        }

        public Task<JsonObject> QueueStateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(JsonNode.Parse(polls < 2 ? """{"queue_running":[[1,"other"]],"queue_pending":[[2,"p1"]]}""" : """{"queue_running":[[2,"p1"]],"queue_pending":[]}""")!.AsObject());

        public Task<byte[]> ViewAsync(string filename, string subfolder, string type, CancellationToken cancellationToken)
        {
            Viewed = (filename, subfolder, type);
            return Task.FromResult(Png);
        }

        public Task CancelAsync(string promptId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task FreeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
