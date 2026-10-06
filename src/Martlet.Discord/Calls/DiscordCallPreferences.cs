using System.Text.Json;
using System.Text.Json.Serialization;

namespace Martlet.Discord.Calls;

/// <summary>What Martlet hears of a Discord call on the owner's own account: only the Discord app (a Windows process
/// loopback of Discord and the processes it started) or everything the PC plays except Martlet.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DiscordCallCapture>))]
public enum DiscordCallCapture { DiscordApp, EverythingButMartlet }

/// <summary>The solid background of the camera view, for OBS's chroma key.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DiscordCameraBackground>))]
public enum DiscordCameraBackground { Green, Blue, Magenta, Black }

/// <summary>Martlet in your own Discord calls (companion mode, <c>discord-calls.json</c>): the owner is in a DM, group DM or
/// server call on their own account and Martlet takes part through this PC. Martlet never controls Discord: it hears the
/// Discord app's sound, optionally looks at the Discord window (on this PC only) to see who is speaking, and speaks into the
/// output the owner picks (a virtual audio cable the owner installed and chose as Discord's microphone). Off by default.</summary>
public sealed record DiscordCallPreferences
{
    public const string FileName = "discord-calls.json";
    public const int MaximumNameLength = 64;

    /// <summary>The call mode is on (it works while always listening runs).</summary>
    public bool On { get; init; }
    public DiscordCallCapture Capture { get; init; } = DiscordCallCapture.DiscordApp;
    /// <summary>Look at the Discord window's speaking indicators to tell who is talking (local only, never sent anywhere).</summary>
    public bool SeeSpeakers { get; init; } = true;
    /// <summary>The owner's own Discord display name: their tile lights up when they (or Martlet through their microphone) talk,
    /// so it is never taken for someone else in the call.</summary>
    public string? OwnerName { get; init; }
    /// <summary>The output Martlet's voice goes to in this mode (a virtual cable's playback side, such as "CABLE Input"), or null
    /// for Martlet's usual output. <see cref="OutputName"/> is kept to show and to find it again.</summary>
    public string? OutputId { get; init; }
    public string? OutputName { get; init; }
    /// <summary>Martlet's voice also plays on its usual output, so the owner hears it too.</summary>
    public bool AlsoSpeakers { get; init; } = true;
    /// <summary>Martlet stops talking when someone in the call talks over it.</summary>
    public bool BargeIn { get; init; } = true;
    public DiscordCameraBackground CameraBackground { get; init; } = DiscordCameraBackground.Green;
    /// <summary>How the character is framed in the camera view: its size (1 fits it to the view's height; 0.25 to 16) and where
    /// its middle sits, as fractions of the view's width and height from its center (+x right, +y up).</summary>
    public double CameraZoom { get; init; } = 1;
    public double CameraX { get; init; }
    public double CameraY { get; init; }

    public const double MinimumCameraZoom = 0.25, MaximumCameraZoom = 16, FarthestCameraOffset = 8;

    /// <summary>The camera background as a color (#RRGGBB).</summary>
    public string CameraColor => Color(CameraBackground);

    public static string Color(DiscordCameraBackground background) => background switch
    {
        DiscordCameraBackground.Blue => "#0047BB",
        DiscordCameraBackground.Magenta => "#FF00FF",
        DiscordCameraBackground.Black => "#000000",
        _ => "#00B140"
    };

    /// <summary>Drops what can't be kept (overlong or control characters), so a loaded file is always usable.</summary>
    public DiscordCallPreferences Normalized()
    {
        var output = string.IsNullOrWhiteSpace(OutputId) || OutputId.Length > 512 ? null : OutputId;
        return this with
        {
            OwnerName = Clean(OwnerName),
            OutputId = output,
            OutputName = output is null ? null : Clean(OutputName, 256),
            Capture = Enum.IsDefined(Capture) ? Capture : DiscordCallCapture.DiscordApp,
            CameraBackground = Enum.IsDefined(CameraBackground) ? CameraBackground : DiscordCameraBackground.Green,
            CameraZoom = double.IsFinite(CameraZoom) ? Math.Clamp(CameraZoom, MinimumCameraZoom, MaximumCameraZoom) : 1,
            CameraX = double.IsFinite(CameraX) ? Math.Clamp(CameraX, -FarthestCameraOffset, FarthestCameraOffset) : 0,
            CameraY = double.IsFinite(CameraY) ? Math.Clamp(CameraY, -FarthestCameraOffset, FarthestCameraOffset) : 0
        };
    }

    /// <summary>The camera framing as people read it: "the character at its fitted size, centered", or its size and how far it
    /// is moved from the middle.</summary>
    [JsonIgnore]
    public string CameraFraming
    {
        get
        {
            var size = Math.Abs(CameraZoom - 1) < 0.005 ? "at its fitted size" : $"at {CameraZoom * 100:0}% of its fitted size";
            static string Part(double value, string plus, string minus) =>
                Math.Abs(value) < 0.0005 ? "" : $"{Math.Abs(value) * 100:0.#}% {(value > 0 ? plus : minus)}";
            var moved = string.Join(" and ", new[] { Part(CameraX, "right", "left"), Part(CameraY, "up", "down") }.Where(p => p.Length > 0));
            return $"the character {size}, " + (moved.Length == 0 ? "centered" : moved + " of center");
        }
    }

    private static string? Clean(string? text, int limit = MaximumNameLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var kept = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return kept.Length == 0 ? null : kept.Length <= limit ? kept : kept[..limit];
    }

    public static DiscordCallPreferences Load(string? directory)
    {
        if (directory is null) return new();
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) return new();
            return (JsonSerializer.Deserialize<DiscordCallPreferences>(File.ReadAllText(path)) ?? new()).Normalized();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public bool Save(string? directory)
    {
        if (directory is null) return false;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            var temporary = Path.Combine(directory, $"discord-calls.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(Normalized()));
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

/// <summary>An output Martlet's voice can go to (a playback device), as Windows lists it.</summary>
public sealed record CallOutput(string Id, string Name, bool IsDefault);

/// <summary>Where Martlet's voice goes in a call, decided from the saved choice and the outputs Windows lists now:
/// <see cref="OutputId"/> null means its usual output; <see cref="AlsoSpeakers"/> plays it on the usual output too.</summary>
public sealed record CallOutputPlan(string? OutputId, string? OutputName, bool AlsoSpeakers, bool Present, string Summary);

/// <summary>Choosing the output for Martlet's voice in a call. The owner installs any virtual cable themselves; Martlet only
/// lists what Windows has and never installs a driver.</summary>
public static class DiscordCallOutputs
{
    private static readonly string[] VirtualCableHints =
        ["CABLE Input", "VB-Audio", "Virtual Cable", "VoiceMeeter Input", "Voicemeeter Input", "Virtual Audio"];

    /// <summary>A playback device that looks like a virtual cable's input (what Discord then uses as its microphone).</summary>
    public static bool LooksVirtual(string name) =>
        VirtualCableHints.Any(hint => name.Contains(hint, StringComparison.OrdinalIgnoreCase));

    /// <summary>The first virtual cable Windows lists, to suggest it.</summary>
    public static CallOutput? Suggest(IReadOnlyList<CallOutput> outputs) => outputs.FirstOrDefault(output => LooksVirtual(output.Name));

    /// <summary>Where Martlet's voice goes now. A chosen output is found by its ID, else by its name (Windows can renumber a
    /// device); a chosen output that isn't connected leaves Martlet's voice on its usual output, so it is never lost. With
    /// <paramref name="outputs"/> null (not listed yet) the saved choice is trusted.</summary>
    public static CallOutputPlan Plan(DiscordCallPreferences preferences, IReadOnlyList<CallOutput>? outputs)
    {
        if (preferences.OutputId is null)
            return new(null, null, false, true, "Martlet's usual output (only you hear it; choose a virtual cable to speak into the call).");
        var found = outputs?.FirstOrDefault(output => output.Id == preferences.OutputId)
            ?? outputs?.FirstOrDefault(output => preferences.OutputName is not null &&
                string.Equals(output.Name, preferences.OutputName, StringComparison.OrdinalIgnoreCase));
        var name = found?.Name ?? preferences.OutputName ?? "The chosen output";
        if (outputs is not null && found is null)
            return new(null, name, false, false, $"{name} isn't connected, so Martlet's voice stays on its usual output.");
        var also = preferences.AlsoSpeakers && found?.IsDefault != true;
        return new(found?.Id ?? preferences.OutputId, name, also, true, $"{name}{(also ? ", and also your usual output" : "")}.");
    }
}
