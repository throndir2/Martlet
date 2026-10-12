using System.Globalization;
using System.Text.Json;
using Martlet.Core.Creations;

namespace Martlet.Conversation;

/// <summary>Body motions as creations (Martlet's shared Creations library, docs/CREATIONS.md): the <c>motion</c> kind, its one
/// asset (the VRMA clip) and its metadata (its name, the prompt it was made from, how long it lasts and what made it). A motion
/// made on one computer is copied to every paired Martlet computer, so every VRM character there can do it again at once
/// without the motion computer (docs/AVATARS.md#kept-motions). The creation tools never offer it
/// (<see cref="CreationKind.ForTools"/>): replies do kept motions by their tags, or with body_motion.</summary>
public static class MotionCreations
{
    public const string KindName = "motion";
    public const string Clip = "clip";
    /// <summary>A VRMA file is binary glTF; it is kept as plain bytes so older Martlet computers and hosts sync it too.</summary>
    public const string MediaType = "application/octet-stream";
    /// <summary>The VRM renderer and the motion computer take clips of at most 4 MiB
    /// (<see cref="Martlet.Core.Motion.MotionFiles.MaximumBytes"/>, Martlet.Avatar.Hosting's VrmAnimationClip.MaximumBytes).</summary>
    public const long MaximumClipBytes = Martlet.Core.Motion.MotionFiles.MaximumBytes;

    /// <summary>The motion kind: done by a VRM character (its tag or body_motion), kept out of the creation tools.</summary>
    public static CreationKind Kind { get; } = new()
    {
        Name = KindName, Noun = "motion", Plural = "motions", Verb = "do", Glyph = "\uE805",
        Assets = [new(Clip, [MediaType], MaximumClipBytes)],
        MaximumBytes = MaximumClipBytes + 1024,
        AutoCleanup = true,
        ForTools = false,
        Describe = creation => Metadata(creation) is { } metadata
            ? $"a {Number(metadata.Seconds)} s body motion" + (creation.Summary is { Length: > 0 } summary ? $" ({summary})" : "")
            : "a body motion",
        Details = creation => string.IsNullOrWhiteSpace(creation.Text) ? [] : [new("Made from", creation.Text.Trim())]
    };

    public sealed record MotionMetadata
    {
        public int Version { get; init; } = 1;
        /// <summary>Its name: the tag VRM characters get for it by default.</summary>
        public string Name { get; init; } = "";
        public double Seconds { get; init; }
        public long? Seed { get; init; }
        public string Model { get; init; } = "";
        public string Where { get; init; } = "";
        public bool Fixture { get; init; }
        /// <summary>How long making it took, in seconds.</summary>
        public double Took { get; init; }
        /// <summary>The character it was first made for (its name), or null.</summary>
        public string? MadeFor { get; init; }
        /// <summary>One of Martlet's starter motions (<see cref="MotionTools.Starters"/>).</summary>
        public bool Starter { get; init; }
    }

    public static MotionMetadata? Metadata(Creation creation)
    {
        ArgumentNullException.ThrowIfNull(creation);
        if (creation.Metadata is not { ValueKind: JsonValueKind.Object } element) return null;
        try { return element.Deserialize<MotionMetadata>(PictureCreations.Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>A new motion creation: its name (title), the motion asked for (summary and text), its length, the clip and who
    /// made it.</summary>
    public static CreationDraft Draft(MotionArguments arguments, MotionMade made, CreationAuthor author, string? madeFor = null, bool starter = false)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(made);
        var motion = arguments.Motion ?? arguments.Name;
        var metadata = new MotionMetadata
        {
            Name = arguments.Name, Seconds = Math.Round(made.Seconds, 2), Seed = made.Seed, Model = made.Model, Where = made.Where,
            Fixture = made.Fixture, Took = Math.Round(made.Took.TotalSeconds, 1),
            MadeFor = madeFor is { Length: > 0 } ? PictureCreations.Clean(madeFor, 80) : null, Starter = starter
        };
        return new()
        {
            Kind = KindName, Title = PictureCreations.Clean(arguments.Name, CreationLibrary.MaximumTitleLength), Text = motion.Trim(),
            Summary = PictureCreations.Clean(made.Fixture ? $"FIXTURE - NOT AI: {motion}" : motion, CreationLibrary.MaximumSummaryLength),
            Duration = TimeSpan.FromSeconds(made.Seconds),
            Metadata = JsonSerializer.SerializeToElement(metadata, PictureCreations.Json), CreatedBy = author,
            Assets = [new(Clip, MediaType, made.Clip)]
        };
    }

    /// <summary>The live motions in <paramref name="library"/>, oldest first (so a new one goes last and the reply instructions'
    /// earlier lines stay the same).</summary>
    public static IReadOnlyList<Creation> Kept(CreationLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        return [.. library.Live.Where(c => c.Kind == KindName).OrderBy(c => c.CreatedAt).ThenBy(c => c.Id, StringComparer.Ordinal)];
    }

    /// <summary>A kept motion's name: its metadata's, else its title as a tag.</summary>
    public static string NameOf(Creation creation) =>
        Metadata(creation)?.Name is { Length: > 0 } name ? name : MotionTools.TagOf(creation.Title) ?? "motion";

    /// <summary>How long a kept motion lasts, in seconds.</summary>
    public static double SecondsOf(Creation creation) =>
        Metadata(creation)?.Seconds is > 0 and var seconds ? seconds : creation.Duration?.TotalSeconds ?? MotionTools.DefaultSeconds;

    /// <summary>A motion creation's clip, or what's missing.</summary>
    public static async Task<(byte[]? Clip, string? Problem)> LoadAsync(Creation creation, ICreationAssets assets, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(creation);
        ArgumentNullException.ThrowIfNull(assets);
        var bytes = await assets.ReadAsync(Clip, token).ConfigureAwait(false);
        if (bytes is null) return (null, "It hasn't reached this computer yet.");
        return IsVrma(bytes) ? (bytes, null) : (null, "It couldn't be read.");
    }

    /// <summary>Whether <paramref name="bytes"/> look like a VRMA clip: binary glTF (it starts with "glTF").</summary>
    public static bool IsVrma(ReadOnlySpan<byte> bytes) => bytes.Length >= 12 && bytes[..4].SequenceEqual("glTF"u8);

    /// <summary>A number of seconds as the model and the log read it: 3, 2.5.</summary>
    public static string Number(double value) => value.ToString(value == Math.Round(value) ? "0" : "0.#", CultureInfo.InvariantCulture);
}
