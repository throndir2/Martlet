using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Core.Pictures;

/// <summary>Where Martlet draws pictures: nowhere (<see cref="Off"/>), Martlet's own <c>pictures</c> host role on this PC or
/// another of the owner's computers (<see cref="Host"/>), a ComfyUI the owner runs at an address (<see cref="ComfyUi"/>), or a
/// paid cloud provider (<see cref="OpenRouter"/>, <see cref="NvidiaBuild"/>).</summary>
public enum PicturePlace { Off, Host, ComfyUi, OpenRouter, NvidiaBuild }

/// <summary>The ComfyUI workflow Martlet runs: Z-Image Turbo (the host role's model; also on any ComfyUI that has its three
/// files), a checkpoint from ComfyUI's checkpoints folder (Stable Diffusion 1.5/XL and their fine-tunes), or the owner's own
/// workflow exported with Export (API) (<see cref="PicturesSettings.WorkflowFile"/>).</summary>
public enum PictureWorkflow { ZImageTurbo, Checkpoint, Custom }

/// <summary>Companion › Pictures on this PC (pictures.json in the data folder; never shared, since which machine draws depends on
/// the computer you talk to). A cloud provider's own key is in Windows Credential Manager (<see cref="CredentialId"/>, a
/// reference); without one it borrows Thinking's key for the same provider. A custom workflow is in pictures-workflow.json.</summary>
public sealed record PicturesSettings
{
    public const string FileName = "pictures.json";
    public const string WorkflowFile = "pictures-workflow.json";
    public const int MaximumWorkflowBytes = 256 * 1024;
    public const string OpenRouterOrigin = ChatCompletionsEndpointCatalog.OpenRouterBaseUrl;
    public const string NvidiaBuildOrigin = ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl;
    /// <summary>NVIDIA Build's image models answer at ai.api.nvidia.com/v1/genai/&lt;model&gt; with the same key as its chat models.</summary>
    public const string NvidiaImageBaseUrl = "https://ai.api.nvidia.com/v1/genai";
    /// <summary>OpenRouter's image models checked 2026-10-06 (api/v1/models?output_modalities=image).</summary>
    public const string OpenRouterDefaultModel = "google/gemini-3.1-flash-image";
    public const string NvidiaDefaultModel = "black-forest-labs/flux.1-schnell";
    public const string DefaultComfyAddress = "http://127.0.0.1:8188";
    private const int MaxFileBytes = 16 * 1024;

    [JsonConverter(typeof(JsonStringEnumConverter<PicturePlace>))]
    public PicturePlace Place { get; init; }
    /// <summary>The paired computer whose pictures role draws (Place Host); null uses the first one that offers it.</summary>
    public string? HostId { get; init; }
    /// <summary>ComfyUI's address, like http://192.168.1.20:8188 (Place ComfyUi).</summary>
    public string? Address { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter<PictureWorkflow>))]
    public PictureWorkflow Workflow { get; init; }
    /// <summary>The checkpoint file in ComfyUI's checkpoints folder (Workflow Checkpoint).</summary>
    public string? Checkpoint { get; init; }
    /// <summary>The cloud provider's image model.</summary>
    public string? ModelId { get; init; }
    /// <summary>The cloud provider's own key in Windows Credential Manager; null uses Thinking's key for the same provider.</summary>
    public Guid? CredentialId { get; init; }
    public DateTimeOffset? ChosenAt { get; init; }

    [JsonIgnore] public bool On => Place != PicturePlace.Off;
    [JsonIgnore] public bool Cloud => Place is PicturePlace.OpenRouter or PicturePlace.NvidiaBuild;
    [JsonIgnore] public bool Comfy => Place is PicturePlace.Host or PicturePlace.ComfyUi;

    /// <summary>The chat endpoint whose key the cloud provider uses (its credential scope): OpenRouter's or NVIDIA Build's.</summary>
    [JsonIgnore]
    public string? Origin => Place switch
    {
        PicturePlace.OpenRouter => OpenRouterOrigin,
        PicturePlace.NvidiaBuild => NvidiaBuildOrigin,
        _ => null
    };

    [JsonIgnore]
    public string Model => ModelId ?? Place switch
    {
        PicturePlace.OpenRouter => OpenRouterDefaultModel,
        PicturePlace.NvidiaBuild => NvidiaDefaultModel,
        _ => ""
    };

    /// <summary>Where pictures are drawn, in words.</summary>
    public string Describe() => Place switch
    {
        PicturePlace.Host => HostId is null ? "Martlet's Pictures role" : $"{HostId}'s Pictures role",
        PicturePlace.ComfyUi => $"ComfyUI at {Address}",
        PicturePlace.OpenRouter => $"OpenRouter ({Model})",
        PicturePlace.NvidiaBuild => $"NVIDIA Build ({Model})",
        _ => "nowhere (off)"
    };

    /// <summary>A ComfyUI address: http or https, a host and port, no credentials, query or fragment; a path is allowed
    /// (ComfyUI behind a reverse proxy). Returned without a trailing slash.</summary>
    public static string ComfyAddress(string value)
    {
        var text = value?.Trim() ?? "";
        if (text.Length > 0 && !text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        ContractRules.Require(text.Length is > 0 and <= 512 && Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
            uri.Host.Length > 0 && !text.Contains('\\'),
            "Enter ComfyUI's address, like http://192.168.1.20:8188 (no user name, password or query).");
        return new Uri(text).GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    /// <summary>Throws when the saved choice isn't usable as it is.</summary>
    public void Validate()
    {
        ContractRules.Defined(Place);
        ContractRules.Defined(Workflow);
        ContractRules.Require(HostId is null or { Length: > 0 and <= 128 } && HostId?.Any(char.IsControl) != true,
            "The computer for pictures is invalid.");
        ContractRules.Require(Checkpoint is null or { Length: > 0 and <= 255 } && Checkpoint?.Any(char.IsControl) != true,
            "The checkpoint name is invalid.");
        if (ModelId is not null) ChatCompletionsSetup.ModelId(ModelId);
        ContractRules.Require(CredentialId != Guid.Empty && (CredentialId is null || Cloud), "The pictures key reference is invalid.");
        if (Place == PicturePlace.ComfyUi)
            ContractRules.Require(Address is not null && ComfyAddress(Address) == Address, "ComfyUI's address is invalid.");
        else ContractRules.Require(Address is null, "Only ComfyUI keeps an address.");
        ContractRules.Require(Place == PicturePlace.ComfyUi || Workflow != PictureWorkflow.Custom || Place == PicturePlace.Host,
            "A custom workflow runs only on ComfyUI.");
    }

    public static PicturesSettings Load(string? directory) => Read(directory).Settings;

    /// <summary>The saved choice and the file's state: none, loaded or unreadable (which reads as off).</summary>
    public static (PicturesSettings Settings, string State) Read(string? directory)
    {
        if (directory is null) return (new(), "none");
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) return (new(), "none");
            if (new FileInfo(path).Length > MaxFileBytes) return (new(), "unreadable");
            var loaded = JsonSerializer.Deserialize<PicturesSettings>(File.ReadAllText(path));
            if (loaded is null) return (new(), "unreadable");
            loaded.Validate();
            return (loaded, "loaded");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or
            ContractException or ArgumentException)
        {
            return (new(), "unreadable");
        }
    }

    /// <summary>Saves pictures.json atomically; false when the data folder can't be written.</summary>
    public bool Save(string? directory)
    {
        if (directory is null) return false;
        Validate();
        return WriteAtomically(directory, FileName, JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>The owner's ComfyUI workflow (Export (API) format), or null when none is saved or it can't be read.</summary>
    public static JsonObject? LoadWorkflow(string? directory) => LoadWorkflow(directory, WorkflowFile);

    /// <summary>A custom workflow a pool member names by its <paramref name="file"/> name in the data directory (a .json file
    /// name, no folders), or null when it isn't one or can't be read.</summary>
    public static JsonObject? LoadWorkflow(string? directory, string file)
    {
        if (directory is null || string.IsNullOrEmpty(file) || Path.GetFileName(file) != file || file.Contains("..", StringComparison.Ordinal) ||
            !file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var path = Path.Combine(directory, file);
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumWorkflowBytes) return null;
            return JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>Saves the owner's ComfyUI workflow after checking it is an API-format workflow (nodes with class_type and inputs).</summary>
    public static bool SaveWorkflow(string? directory, JsonObject workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        if (directory is null) return false;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(workflow);
        ContractRules.Require(bytes.Length <= MaximumWorkflowBytes, "The workflow is too large (256 KB at most).");
        return WriteAtomically(directory, WorkflowFile, bytes);
    }

    private static bool WriteAtomically(string directory, string name, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, name);
            var temporary = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(name)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temporary, bytes);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The cloud provider's own key scope: a Chat Completions key bound to that provider's API base URL, so Thinking can
    /// share it and it is never sent anywhere else.</summary>
    public CredentialBinding Binding(Guid profileId, Guid credentialId) =>
        new(profileId, credentialId, SetupRole.Llm, SetupRouteType.ChatCompletions, ChatCompletionsSetup.Alias,
            Origin ?? throw new InvalidOperationException("Only a cloud provider has a key."));

    /// <summary>Whether it borrows the Thinking route's key: a cloud provider without a key of its own, and Thinking on the same
    /// provider with one.</summary>
    public bool UsesThinkingKey(SetupRoute? thinking) =>
        Cloud && CredentialId is null &&
        thinking is { RouteType: SetupRouteType.ChatCompletions, CredentialId: not null } && thinking.Origin == Origin;
}
