using System.Buffers.Binary;
using System.Text;

namespace Martlet.Core.Motion;

/// <summary>
/// Makes one body motion for a VRM character from an English description (docs/MOTION_HOST.md) and returns it as a VRMA clip
/// (VRM Animation: binary glTF with the <c>VRMC_vrm_animation</c> 1.0 extension). Implementations: Martlet's <c>motion</c>
/// host role (HY-Motion 1.0 on a paired computer's NVIDIA GPU, gateway route <c>martlet.gateway.motion.v1</c>) and
/// <see cref="FixtureMotionMaker"/>, a deterministic FIXTURE - NOT AI stand-in for tests. A motion takes seconds to a few
/// minutes (the first one loads the model), so callers run it as a background job.
/// </summary>
public interface IMotionMaker
{
    /// <summary>Where motions are made, in words ("studio's Motion role").</summary>
    string Where { get; }

    /// <summary>Whether a motion can be made now. Never throws for "not set up": it returns <see cref="MotionMakerAvailability.Available"/>
    /// false with a reason the owner can act on.</summary>
    Task<MotionMakerAvailability> GetAvailabilityAsync(CancellationToken cancellationToken);

    /// <summary>Makes one motion. Cancelling <paramref name="cancellationToken"/> throws <see cref="OperationCanceledException"/>;
    /// other failures throw <see cref="MotionException"/> with a stable code (<see cref="MotionErrorCodes"/>).</summary>
    Task<MotionResult> GenerateAsync(MotionRequest request, CancellationToken cancellationToken);
}

/// <summary>One motion to make: an English description of body movement (<see cref="Prompt"/>, written by Martlet), its length
/// and an optional seed. HY-Motion does not make emotions or looks, objects or scenes, two or more people, seamless loops or
/// motions in place; it works best with prompts under 30 words and motions under 5 seconds.</summary>
public sealed record MotionRequest
{
    public const double MinimumSeconds = 1;
    public const double MaximumSeconds = 12;
    public const double DefaultSeconds = 4;
    public const int MinimumPromptCharacters = 3;
    public const int MaximumPromptCharacters = 400;
    public const int MaximumPromptWords = 60;
    public const long MaximumSeed = int.MaxValue;

    public required string Prompt { get; init; }
    public double Seconds { get; init; } = DefaultSeconds;
    /// <summary>Optional seed for a repeatable motion; a random one is chosen when null.</summary>
    public long? Seed { get; init; }

    /// <summary>Throws <see cref="MotionException"/> (<c>request.invalid</c>) when a field is outside its bounds.</summary>
    public void Validate()
    {
        var prompt = Prompt?.Trim() ?? "";
        if (prompt.Length is < MinimumPromptCharacters or > MaximumPromptCharacters || prompt.Any(char.IsControl) ||
            !prompt.Any(char.IsAsciiLetter))
            throw new MotionException(MotionErrorCodes.RequestInvalid,
                $"A motion's description must be {MinimumPromptCharacters}-{MaximumPromptCharacters} characters of English text.");
        if (Words(prompt) > MaximumPromptWords)
            throw new MotionException(MotionErrorCodes.RequestInvalid, $"A motion's description must be at most {MaximumPromptWords} words.");
        if (!double.IsFinite(Seconds) || Seconds is < MinimumSeconds or > MaximumSeconds)
            throw new MotionException(MotionErrorCodes.RequestInvalid,
                $"A motion must be {MinimumSeconds:0}-{MaximumSeconds:0} seconds long.");
        if (Seed is < 0 or > MaximumSeed)
            throw new MotionException(MotionErrorCodes.RequestInvalid, $"The seed must be 0-{MaximumSeed}.");
    }

    public static int Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}

/// <summary>A made motion: the VRMA bytes (<see cref="MotionFiles.MediaType"/>), its frames and frame rate, the seed, what
/// made it (<see cref="Engine"/> "hy-motion" or "fixture"; <see cref="Model"/>), where, and how long it took.</summary>
public sealed record MotionResult
{
    public required byte[] Vrma { get; init; }
    public int Frames { get; init; }
    public int Fps { get; init; }
    public double Seconds { get; init; }
    public long? Seed { get; init; }
    public required string Engine { get; init; }
    public string Model { get; init; } = "";
    public string Where { get; init; } = "";
    public bool Fixture { get; init; }
    public TimeSpan Took { get; init; }
}

public sealed record MotionMakerAvailability(bool Available, string? Reason, string? Where, bool Fixture = false)
{
    public static MotionMakerAvailability Unavailable(string reason) => new(false, reason, null);
}

/// <summary>Stable failure codes of <see cref="MotionException"/>.</summary>
public static class MotionErrorCodes
{
    /// <summary>Motion isn't set up, or the place isn't reachable, provisioned or ready.</summary>
    public const string Unavailable = "motion.unavailable";
    /// <summary>The place is making another motion.</summary>
    public const string Busy = "motion.busy";
    /// <summary>The graphics card ran out of memory (other roles use it, or the card is too small).</summary>
    public const string OutOfMemory = "motion.out_of_memory";
    /// <summary>A request field is outside its bounds.</summary>
    public const string RequestInvalid = "request.invalid";
    public const string TimedOut = "motion.timeout";
    public const string Failed = "motion.failed";
}

public sealed class MotionException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>Recognizes VRMA bytes (binary glTF 2.0 whose JSON uses <c>VRMC_vrm_animation</c>) without decoding them.</summary>
public static class MotionFiles
{
    public const string MediaType = "model/gltf-binary";
    public const string Extension = ".vrma";
    /// <summary>The largest VRMA Martlet keeps (12 seconds of 52 bones at 30 frames a second is about 0.4 MB).</summary>
    public const int MaximumBytes = 4 * 1024 * 1024;

    /// <summary>Whether <paramref name="bytes"/> is a VRMA clip: a glTF binary of version 2 whose length is its own, with a JSON
    /// chunk first that names the VRMC_vrm_animation extension.</summary>
    public static bool IsVrma(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 28 or > MaximumBytes || !bytes[..4].SequenceEqual("glTF"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != 2 || BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != bytes.Length)
            return false;
        var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        if (length == 0 || length > bytes.Length - 20 || !bytes.Slice(16, 4).SequenceEqual("JSON"u8)) return false;
        return Encoding.UTF8.GetString(bytes.Slice(20, (int)length)).Contains("\"VRMC_vrm_animation\"", StringComparison.Ordinal);
    }

    /// <summary>Checks a clip a place returned: VRMA no larger than <see cref="MaximumBytes"/>.</summary>
    public static void Require(byte[] bytes, string where)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > MaximumBytes) throw new MotionException(MotionErrorCodes.Failed, $"{where} returned a motion that is too large.");
        if (!IsVrma(bytes)) throw new MotionException(MotionErrorCodes.Failed, $"{where} returned something that isn't a VRMA motion.");
    }
}
