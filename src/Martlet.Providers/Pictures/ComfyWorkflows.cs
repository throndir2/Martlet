using System.Text.Json.Nodes;
using Martlet.Core.Pictures;

namespace Martlet.Providers.Pictures;

/// <summary>The ComfyUI workflows Martlet runs (API format, as ComfyUI's Export (API) writes them): Z-Image Turbo (the
/// <c>pictures</c> host role's model, ComfyUI's own example), a plain checkpoint workflow for Stable Diffusion 1.5/XL
/// checkpoints, or the owner's workflow with the request filled in.</summary>
public static class ComfyWorkflows
{
    public const string ZImageModel = "z_image_turbo_bf16.safetensors";
    public const string ZImageTextEncoder = "qwen_3_4b.safetensors";
    public const string ZImageVae = "ae.safetensors";
    public const string FilePrefix = "martlet/picture";
    private const string DefaultNegative = "blurry, low quality, deformed, watermark, text artifacts";

    /// <summary>The model files a workflow needs (folder, file), to check before queueing.</summary>
    public static IReadOnlyList<(string Folder, string File)> Needs(PictureWorkflow workflow, string? checkpoint) => workflow switch
    {
        PictureWorkflow.ZImageTurbo => [("diffusion_models", ZImageModel), ("text_encoders", ZImageTextEncoder), ("vae", ZImageVae)],
        PictureWorkflow.Checkpoint when checkpoint is not null => [("checkpoints", checkpoint)],
        _ => []
    };

    /// <summary>The workflow for <paramref name="request"/> with <paramref name="seed"/>.</summary>
    public static JsonObject Build(PictureWorkflow workflow, PictureRequest request, long seed, string? checkpoint = null, JsonObject? custom = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        return workflow switch
        {
            PictureWorkflow.ZImageTurbo => ZImage(request, seed),
            PictureWorkflow.Checkpoint => Checkpoint(request, seed, checkpoint ??
                throw new PictureException(PictureErrorCodes.RequestInvalid, "Choose a checkpoint for ComfyUI first.")),
            _ => Fill(custom ?? throw new PictureException(PictureErrorCodes.RequestInvalid, "Load your ComfyUI workflow first."), request, seed)
        };
    }

    /// <summary>ComfyUI's Z-Image Turbo example: 9 steps, cfg 1, euler/simple.</summary>
    public static JsonObject ZImage(PictureRequest request, long seed) => new()
    {
        ["3"] = Node("KSampler", new()
        {
            ["seed"] = seed, ["steps"] = 9, ["cfg"] = 1.0, ["sampler_name"] = "euler", ["scheduler"] = "simple", ["denoise"] = 1.0,
            ["model"] = Link("16"), ["positive"] = Link("6"), ["negative"] = Link("7"), ["latent_image"] = Link("13")
        }),
        ["6"] = Node("CLIPTextEncode", new() { ["text"] = request.Prompt, ["clip"] = Link("18") }),
        ["7"] = Node("CLIPTextEncode", new() { ["text"] = request.NegativePrompt ?? DefaultNegative, ["clip"] = Link("18") }),
        ["8"] = Node("VAEDecode", new() { ["samples"] = Link("3"), ["vae"] = Link("17") }),
        ["9"] = Node("SaveImage", new() { ["filename_prefix"] = FilePrefix, ["images"] = Link("8") }),
        ["13"] = Node("EmptySD3LatentImage", new() { ["width"] = request.Width, ["height"] = request.Height, ["batch_size"] = 1 }),
        ["16"] = Node("UNETLoader", new() { ["unet_name"] = ZImageModel, ["weight_dtype"] = "default" }),
        ["17"] = Node("VAELoader", new() { ["vae_name"] = ZImageVae }),
        ["18"] = Node("CLIPLoader", new() { ["clip_name"] = ZImageTextEncoder, ["type"] = "lumina2", ["device"] = "default" })
    };

    /// <summary>Whether a checkpoint looks like Stable Diffusion XL (or a model trained like it), which draws at about 1024 pixels;
    /// others are drawn at about 512-768 pixels.</summary>
    public static bool LooksXl(string checkpoint)
    {
        var name = checkpoint.ToLowerInvariant();
        return name.Contains("xl", StringComparison.Ordinal) || name.Contains("pony", StringComparison.Ordinal) ||
            name.Contains("illustrious", StringComparison.Ordinal) || name.Contains("noob", StringComparison.Ordinal) ||
            name.Contains("turbo", StringComparison.Ordinal) || name.Contains("lightning", StringComparison.Ordinal);
    }

    /// <summary>A checkpoint's size for a shape: the request's for XL, about two thirds of it (multiples of 64) otherwise.</summary>
    public static (int Width, int Height) CheckpointSize(PictureRequest request, string checkpoint)
    {
        if (LooksXl(checkpoint)) return (request.Width, request.Height);
        static int Scale(int pixels) => Math.Max(384, (int)Math.Round(pixels * 2 / 3.0 / 64) * 64);
        return request.Shape == PictureShape.Square ? (512, 512) : (Scale(request.Width), Scale(request.Height));
    }

    /// <summary>A plain checkpoint workflow: 25 steps (8 for turbo/lightning checkpoints), dpmpp_2m karras.</summary>
    public static JsonObject Checkpoint(PictureRequest request, long seed, string checkpoint)
    {
        var (width, height) = CheckpointSize(request, checkpoint);
        var fast = checkpoint.Contains("turbo", StringComparison.OrdinalIgnoreCase) || checkpoint.Contains("lightning", StringComparison.OrdinalIgnoreCase);
        return new()
        {
            ["3"] = Node("KSampler", new()
            {
                ["seed"] = seed, ["steps"] = fast ? 8 : 25, ["cfg"] = fast ? 2.0 : 6.5, ["sampler_name"] = fast ? "euler_ancestral" : "dpmpp_2m",
                ["scheduler"] = fast ? "normal" : "karras", ["denoise"] = 1.0,
                ["model"] = Link("4"), ["positive"] = Link("6"), ["negative"] = Link("7"), ["latent_image"] = Link("5")
            }),
            ["4"] = Node("CheckpointLoaderSimple", new() { ["ckpt_name"] = checkpoint }),
            ["5"] = Node("EmptyLatentImage", new() { ["width"] = width, ["height"] = height, ["batch_size"] = 1 }),
            ["6"] = Node("CLIPTextEncode", new() { ["text"] = request.Prompt, ["clip"] = Link("4", 1) }),
            ["7"] = Node("CLIPTextEncode", new() { ["text"] = request.NegativePrompt ?? DefaultNegative, ["clip"] = Link("4", 1) }),
            ["8"] = Node("VAEDecode", new() { ["samples"] = Link("3"), ["vae"] = Link("4", 2) }),
            ["9"] = Node("SaveImage", new() { ["filename_prefix"] = FilePrefix, ["images"] = Link("8") })
        };
    }

    /// <summary>The owner's workflow with the request filled in. Placeholders anywhere in its text inputs are replaced:
    /// <c>{{prompt}}</c>, <c>{{negative}}</c>; an input that is exactly <c>{{seed}}</c>, <c>{{width}}</c> or <c>{{height}}</c>
    /// becomes that number. Without a <c>{{prompt}}</c>, the text of the node feeding each sampler's positive input is replaced
    /// (and its negative's, when the request has one); samplers' seeds and empty latents' sizes are set either way unless the
    /// workflow uses those placeholders.</summary>
    public static JsonObject Fill(JsonObject workflow, PictureRequest request, long seed)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(request);
        var filled = (JsonObject)workflow.DeepClone();
        var nodes = filled.Where(p => p.Value is JsonObject node && node["class_type"] is JsonValue && node["inputs"] is JsonObject)
            .Select(p => (Id: p.Key, Node: (JsonObject)p.Value!)).ToArray();
        if (nodes.Length == 0 || nodes.Length != filled.Count)
            throw new PictureException(PictureErrorCodes.RequestInvalid,
                "That isn't a ComfyUI API workflow. In ComfyUI, use Workflow › Export (API) and load that file.");
        var placed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, node) in nodes)
        {
            var inputs = (JsonObject)node["inputs"]!;
            foreach (var name in inputs.Select(p => p.Key).ToArray())
            {
                if (inputs[name] is not JsonValue value || value.ToString() is not { } text || !text.Contains("{{", StringComparison.Ordinal)) continue;
                switch (text.Trim())
                {
                    case "{{seed}}": inputs[name] = seed; placed.Add("seed"); continue;
                    case "{{width}}": inputs[name] = request.Width; placed.Add("size"); continue;
                    case "{{height}}": inputs[name] = request.Height; placed.Add("size"); continue;
                }
                if (text.Contains("{{prompt}}", StringComparison.Ordinal)) placed.Add("prompt");
                if (text.Contains("{{negative}}", StringComparison.Ordinal)) placed.Add("negative");
                inputs[name] = text.Replace("{{prompt}}", request.Prompt, StringComparison.Ordinal)
                    .Replace("{{negative}}", request.NegativePrompt ?? "", StringComparison.Ordinal);
            }
        }
        var samplers = nodes.Where(n => n.Node["class_type"]!.ToString() is var type &&
            (type.StartsWith("KSampler", StringComparison.Ordinal) || type.StartsWith("SamplerCustom", StringComparison.Ordinal))).ToArray();
        if (!placed.Contains("prompt"))
        {
            foreach (var (_, sampler) in samplers)
            {
                if (TextNode(filled, sampler["inputs"]!["positive"]) is { } positive)
                {
                    positive["text"] = request.Prompt;
                    placed.Add("prompt");
                }
                if (request.NegativePrompt is { } negative && TextNode(filled, sampler["inputs"]!["negative"]) is { } against) against["text"] = negative;
            }
            if (!placed.Contains("prompt"))
                throw new PictureException(PictureErrorCodes.RequestInvalid,
                    "Martlet can't tell where the description goes in your workflow. Put {{prompt}} in its prompt text and load it again.");
        }
        if (!placed.Contains("seed"))
            foreach (var (_, node) in nodes)
            {
                var inputs = (JsonObject)node["inputs"]!;
                foreach (var name in new[] { "seed", "noise_seed" })
                    if (inputs[name] is JsonValue current && current.TryGetValue<double>(out _)) inputs[name] = seed;
            }
        if (!placed.Contains("size"))
            foreach (var (_, node) in nodes.Where(n => n.Node["class_type"]!.ToString() is var type &&
                         type.StartsWith("Empty", StringComparison.Ordinal) && type.Contains("Latent", StringComparison.Ordinal)))
            {
                var inputs = (JsonObject)node["inputs"]!;
                if (inputs["width"] is JsonValue) inputs["width"] = request.Width;
                if (inputs["height"] is JsonValue) inputs["height"] = request.Height;
            }
        return filled;
    }

    // The node a link points at, when it holds a plain text input (CLIPTextEncode and its kin).
    private static JsonObject? TextNode(JsonObject workflow, JsonNode? link)
    {
        if (link is not JsonArray { Count: 2 } pair || pair[0]?.ToString() is not { } id || workflow[id] is not JsonObject node) return null;
        return node["inputs"] is JsonObject inputs && inputs["text"] is JsonValue text && text.TryGetValue<string>(out _) ? inputs : null;
    }

    private static JsonObject Node(string type, JsonObject inputs) => new() { ["inputs"] = inputs, ["class_type"] = type };

    private static JsonArray Link(string node, int output = 0) => new(node, output);
}
