using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Creations;
using Martlet.Core.Pictures;

namespace Martlet.Conversation;

/// <summary>Pictures as creations (Martlet's shared Creations library, docs/CREATIONS.md): the <c>picture</c> kind, its one asset
/// (the picture as PNG, JPEG or WebP) and its metadata (the description it was drawn from, its shape, size and what drew it).
/// A picture drawn on one computer is copied to every paired Martlet computer, so any of them can show it.</summary>
public static class PictureCreations
{
    public const string KindName = "picture";
    public const string Image = "image";

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>The picture kind: shown by Martlet in the talk window (perform_creation).</summary>
    public static CreationKind Kind { get; } = new()
    {
        Name = KindName, Noun = "picture", Plural = "pictures", Verb = "show", Glyph = "\uE8B9",
        Assets = [new(Image, [.. PictureImages.MediaTypes], PictureImages.MaximumBytes)],
        MaximumBytes = PictureImages.MaximumBytes + 1024,
        AutoCleanup = true,
        Describe = creation => Metadata(creation) is { } metadata
            ? $"a {metadata.Shape} picture" + (creation.Summary is { Length: > 0 } summary ? $" ({summary})" : "")
            : "a picture",
        Details = creation => string.IsNullOrWhiteSpace(creation.Text) ? [] : [new("Drawn from", creation.Text.Trim())]
    };

    public sealed record PictureMetadata
    {
        public int Version { get; init; } = 1;
        public string Shape { get; init; } = "square";
        public int Width { get; init; }
        public int Height { get; init; }
        public string Engine { get; init; } = "";
        public string Model { get; init; } = "";
        public string Where { get; init; } = "";
        public long? Seed { get; init; }
        public bool Fixture { get; init; }
        public double Seconds { get; init; }
    }

    public static PictureMetadata? Metadata(Creation creation)
    {
        if (creation.Metadata is not { ValueKind: JsonValueKind.Object } element) return null;
        try { return element.Deserialize<PictureMetadata>(Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>A new picture creation for <paramref name="result"/>: its title, what the user asked for (its summary), the
    /// description it was drawn from (its text) and who drew it.</summary>
    public static CreationDraft Draft(PictureResult result, string title, string about, PictureRequest request, CreationAuthor author)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(request);
        var metadata = new PictureMetadata
        {
            Shape = PictureShapes.Name(request.Shape), Width = result.Width, Height = result.Height, Engine = result.Engine, Model = result.Model,
            Where = result.Where, Seed = result.Seed, Fixture = result.Fixture, Seconds = Math.Round(result.Took.TotalSeconds, 1)
        };
        return new()
        {
            Kind = KindName, Title = Clean(title, CreationLibrary.MaximumTitleLength), Text = request.Prompt.Trim(),
            Summary = Clean(result.Fixture ? $"FIXTURE - NOT AI: {about}" : about, CreationLibrary.MaximumSummaryLength),
            Metadata = JsonSerializer.SerializeToElement(metadata, Json), CreatedBy = author,
            Assets = [new(Image, result.MediaType, result.Image)]
        };
    }

    /// <summary>The picture creation <paramref name="reference"/> names (its key or ID), or null.</summary>
    public static Creation? Find(string dataDirectory, string? reference) =>
        CreationStore.View(dataDirectory).Resolve(reference) is { Kind: KindName } creation ? creation : null;

    /// <summary>A picture creation's bytes and media type, or what's missing.</summary>
    public static async Task<(byte[]? Image, string? MediaType, string? Problem)> LoadAsync(Creation creation, ICreationAssets assets, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(creation);
        ArgumentNullException.ThrowIfNull(assets);
        var bytes = await assets.ReadAsync(Image, token).ConfigureAwait(false);
        if (bytes is null) return (null, null, "It hasn't reached this computer yet.");
        return PictureImages.Probe(bytes) is { } probe ? (bytes, probe.MediaType, null) : (null, null, "It couldn't be read.");
    }

    internal static string Clean(string? text, int limit)
    {
        var line = string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new string([.. word.Where(c => !char.IsControl(c))])));
        return line.Length <= limit ? line : line[..limit].TrimEnd();
    }

    internal static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);
}
