using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Martlet.Core.Settings;

/// <summary>How Chatterbox Original speaks (Companion › Voice › Chatterbox Original style). Each of its two ways of speaking is
/// an exaggeration (how much emotion: 0.25-2, 0.5 is neutral, extreme values can be unstable) and a CFG weight (how closely it
/// follows the voice and the words: 0-1; lower is slower and more deliberate). <see cref="GeneralExaggeration"/> and
/// <see cref="GeneralCfgWeight"/> are for sentences without a tag; the Expressive pair is for a sentence the reply starts with
/// [expressive]. The defaults are Resemble AI's tips (general 0.5 and 0.5; expressive or dramatic speech about 0.7 and 0.3).
/// It is kept in <see cref="FileName"/>, the same on all the owner's computers (the household setting <see cref="SharedKey"/>,
/// beside the voices it speaks with), and sent with every reply to that engine; Turbo and Nano have no such setting.</summary>
public sealed record ChatterboxStyle(
    double GeneralExaggeration = 0.5,
    double GeneralCfgWeight = 0.5,
    double ExpressiveExaggeration = 0.7,
    double ExpressiveCfgWeight = 0.3)
{
    public const string FileName = "chatterbox-style.json";
    public const double MinimumExaggeration = 0.25, MaximumExaggeration = 2.0, MinimumCfgWeight = 0.0, MaximumCfgWeight = 1.0;

    public static ChatterboxStyle Default { get; } = new();

    /// <summary>Whether every value is within its range (what the gateway and the voice service accept).</summary>
    public bool IsValid =>
        Exaggeration(GeneralExaggeration) && Exaggeration(ExpressiveExaggeration) && Cfg(GeneralCfgWeight) && Cfg(ExpressiveCfgWeight);

    private static bool Exaggeration(double value) => double.IsFinite(value) && value is >= MinimumExaggeration and <= MaximumExaggeration;

    private static bool Cfg(double value) => double.IsFinite(value) && value is >= MinimumCfgWeight and <= MaximumCfgWeight;

    /// <summary>"General: exaggeration 0.5, CFG weight 0.5; expressive: exaggeration 0.7, CFG weight 0.3." (one sentence).</summary>
    public string Describe() =>
        $"General: exaggeration {Number(GeneralExaggeration)}, CFG weight {Number(GeneralCfgWeight)}; " +
        $"expressive: exaggeration {Number(ExpressiveExaggeration)}, CFG weight {Number(ExpressiveCfgWeight)}.";

    public static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>The style as the request sends it: {"general": {"exaggeration", "cfg_weight"}, "expressive": {...}}.</summary>
    public JsonObject Wire() => new()
    {
        ["general"] = new JsonObject { ["exaggeration"] = GeneralExaggeration, ["cfg_weight"] = GeneralCfgWeight },
        ["expressive"] = new JsonObject { ["exaggeration"] = ExpressiveExaggeration, ["cfg_weight"] = ExpressiveCfgWeight }
    };

    /// <summary>The style saved in <paramref name="dataDirectory"/>, or <see cref="Default"/> when none is saved or it can't be
    /// read or is out of range.</summary>
    public static ChatterboxStyle Load(string dataDirectory)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dataDirectory, FileName)));
            var root = document.RootElement;
            double Value(string group, string name) => root.GetProperty(group).GetProperty(name).GetDouble();
            var style = new ChatterboxStyle(Value("general", "exaggeration"), Value("general", "cfgWeight"),
                Value("expressive", "exaggeration"), Value("expressive", "cfgWeight"));
            return style.IsValid ? style : Default;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException
            or InvalidOperationException or FormatException)
        {
            return Default;
        }
    }

    /// <summary>Saves this style in <paramref name="dataDirectory"/> (replacing the file whole). Throws for a value out of
    /// range.</summary>
    public void Save(string dataDirectory)
    {
        if (!IsValid) throw new ArgumentOutOfRangeException(nameof(dataDirectory), "A Chatterbox style value is out of range.");
        Directory.CreateDirectory(dataDirectory);
        var json = new JsonObject
        {
            ["general"] = new JsonObject { ["exaggeration"] = GeneralExaggeration, ["cfgWeight"] = GeneralCfgWeight },
            ["expressive"] = new JsonObject { ["exaggeration"] = ExpressiveExaggeration, ["cfgWeight"] = ExpressiveCfgWeight }
        };
        var path = Path.Combine(dataDirectory, FileName);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>The household setting that carries the style to the owner's other computers.</summary>
    public const string SharedKey = "chatterbox-style";

    /// <summary>The style as it travels: {"general": {...}, "expressive": {...}}, as the request sends it.</summary>
    public string Share() => Wire().ToJsonString();

    /// <summary>A travelled style, or null when it isn't one this Martlet can use.</summary>
    public static ChatterboxStyle? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement;
            double Value(string group, string name) => root.GetProperty(group).GetProperty(name).GetDouble();
            var style = new ChatterboxStyle(Value("general", "exaggeration"), Value("general", "cfg_weight"),
                Value("expressive", "exaggeration"), Value("expressive", "cfg_weight"));
            return style.IsValid ? style : null;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>The household setting backed by <see cref="FileName"/> in <paramref name="directory"/>.</summary>
    public static Sync.ISharedSection Section(string directory) => new Sync.DelegateSection(SharedKey, "Chatterbox Original style", _ =>
    {
        var path = Path.Combine(directory, FileName);
        DateTimeOffset? changed = File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null;
        return Task.FromResult<Sync.SharedLocal?>(new(Load(directory).Share(), null, changed is null, changed));
    }, (setting, _) =>
    {
        if (Parse(setting.Value) is not { } style) return Task.FromResult(Sync.SharedApply.Waiting("It was chosen on a newer Martlet; update this PC to use it."));
        if (style == Load(directory) && File.Exists(Path.Combine(directory, FileName))) return Task.FromResult(Sync.SharedApply.Done);
        try { style.Save(directory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return Task.FromResult(Sync.SharedApply.Waiting("It couldn't be saved on this PC.")); }
        return Task.FromResult(Sync.SharedApply.Done);
    }, () => Default.Share());
}
