using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

/// <summary>The kinds of input besides text that a model can take: pictures (the screen, a camera) and recordings (your voice,
/// what this PC plays).</summary>
public enum SenseKind { Image, Audio }

/// <summary>Which model takes one kind of input (<see cref="SenseModel"/>). Thinking, the text model, always writes Martlet's
/// reply (docs/SENSE_MODELS.md).</summary>
public enum SenseSource
{
    /// <summary>Use the same model as the text model: Thinking takes the pictures or recordings itself (an omni model). The
    /// default.</summary>
    Thinking,
    /// <summary>Use the same model as the other kind of input: the audio model for pictures, the image model for recordings.</summary>
    OtherSense,
    /// <summary>A model of its own (<see cref="SenseModel.Own"/>): it puts what it sees or hears into words for Thinking.</summary>
    Own
}

/// <summary>The model chosen for one kind of input.</summary>
public sealed record SenseModel
{
    [JsonConverter(typeof(JsonStringEnumConverter<SenseSource>))]
    public SenseSource Source { get; init; }

    /// <summary>The model of its own (<see cref="SenseSource.Own"/>): an OpenAI-compatible endpoint (Ollama on this PC, a cloud
    /// provider or another server; Place Endpoint, with its own key in Windows Credential Manager or Thinking's key for the same
    /// base URL) or a paired computer's Ollama (Place Host; pictures only, because a paired computer's gateway takes no
    /// recordings). Null for the other sources.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DeepThinkingSettings? Own { get; init; }

    /// <summary>When the owner chose it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ChosenAt { get; init; }

    /// <summary>Throws when this choice isn't usable for <paramref name="kind"/> as it is.</summary>
    public void Validate(SenseKind kind)
    {
        ContractRules.Defined(kind);
        ContractRules.Defined(Source);
        if (Source != SenseSource.Own)
        {
            ContractRules.Require(Own is null, "Only a model of its own keeps a destination.");
            return;
        }
        ContractRules.Require(Own is { Pool: null, Place: DeepThinkingPlace.Endpoint or DeepThinkingPlace.Host },
            "A model of its own is an OpenAI-compatible endpoint or a paired computer's model.");
        ContractRules.Require(kind == SenseKind.Image || Own!.Place == DeepThinkingPlace.Endpoint,
            "A paired computer's gateway takes no recordings. Choose Ollama on this PC, a cloud provider or another server for the audio model.");
        Own!.Validate();
    }
}

/// <summary>Companion › Vision › Image model and Companion › Listening › Audio model, on this PC (sense-models.json in the data
/// folder; never shared, because which models a computer can run beside Thinking depends on its hardware). Thinking, the text
/// model, always writes Martlet's reply. By default it also takes the pictures and recordings itself (an omni model, such as
/// Gemma 4 E2B). An image or audio model of its own instead puts what it sees or hears into words, and the words go to Thinking.
/// Each kind may also use the same model as the other kind. See docs/SENSE_MODELS.md.</summary>
public sealed record SenseModels
{
    public const string FileName = "sense-models.json";
    private const int MaxFileBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public int SchemaVersion { get; init; } = 1;

    /// <summary>The image model: what takes pictures of the screen and the camera.</summary>
    public SenseModel Image { get => image; init => image = value ?? new(); }
    private readonly SenseModel image = new();

    /// <summary>The audio model: what takes recordings of your voice and of what this PC plays.</summary>
    public SenseModel Audio { get => audio; init => audio = value ?? new(); }
    private readonly SenseModel audio = new();

    /// <summary>The choice for <paramref name="kind"/>.</summary>
    public SenseModel For(SenseKind kind) => kind == SenseKind.Image ? Image : Audio;

    /// <summary>These choices with <paramref name="model"/> for <paramref name="kind"/>.</summary>
    public SenseModels With(SenseKind kind, SenseModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return kind == SenseKind.Image ? this with { Image = model } : this with { Audio = model };
    }

    /// <summary>The other kind of input: recordings for pictures, pictures for recordings.</summary>
    public static SenseKind Other(SenseKind kind) => kind == SenseKind.Image ? SenseKind.Audio : SenseKind.Image;

    /// <summary>The model that takes <paramref name="kind"/> once "the same model as the other kind" is followed: the model of its
    /// own, or null for the text model (Thinking) itself. "The same as the other kind" both ways reads as the text model.</summary>
    public DeepThinkingSettings? Place(SenseKind kind)
    {
        var chosen = For(kind);
        if (chosen.Source == SenseSource.OtherSense) chosen = For(Other(kind));
        return chosen is { Source: SenseSource.Own, Own: { } own } ? own : null;
    }

    /// <summary>Whether both kinds go to the same model of its own.</summary>
    [JsonIgnore]
    public bool OneModel => Place(SenseKind.Image) is { } image && Place(SenseKind.Audio) is { } audio && image.Key == audio.Key;

    /// <summary>Whether no kind has a model of its own: Thinking takes everything, as before this setting existed.</summary>
    [JsonIgnore]
    public bool AllThinking => Place(SenseKind.Image) is null && Place(SenseKind.Audio) is null;

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == 1, "The image and audio model file is from a newer Martlet.");
        Image.Validate(SenseKind.Image);
        Audio.Validate(SenseKind.Audio);
    }

    public static SenseModels Load(string? directory) => Read(directory).Settings;

    /// <summary>The saved choices and the file's state: none, loaded or unreadable (which reads as the text model for both).</summary>
    public static (SenseModels Settings, string State) Read(string? directory)
    {
        if (directory is null) return (new(), "none");
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) return (new(), "none");
            if (new FileInfo(path).Length > MaxFileBytes) return (new(), "unreadable");
            var loaded = JsonSerializer.Deserialize<SenseModels>(File.ReadAllText(path));
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

    /// <summary>Saves sense-models.json atomically; false when the data folder can't be written.</summary>
    public bool Save(string? directory)
    {
        if (directory is null) return false;
        Validate();
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            var temporary = Path.Combine(directory, $"sense-models.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(this, Indented));
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
}
