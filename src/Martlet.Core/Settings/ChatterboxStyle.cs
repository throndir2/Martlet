using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Martlet.Core.Settings;

/// <summary>How Chatterbox Original speaks (Companion › Voice › Chatterbox Original style). Each of its two ways of speaking is
/// an exaggeration (how much emotion: 0.25-2, 0.5 is neutral, extreme values can be unstable) and a CFG weight (how closely it
/// follows the voice and the words: 0-1; lower is slower and more deliberate). <see cref="GeneralExaggeration"/> and
/// <see cref="GeneralCfgWeight"/> are for sentences without a tag; the Expressive pair is for a sentence the reply starts with
/// [expressive]. The defaults are Resemble AI's tips (general 0.5 and 0.5; expressive or dramatic speech about 0.7 and 0.3).
/// It is kept on this PC (<see cref="FileName"/>) and sent with every reply to that engine; Turbo and Nano have no such
/// setting.</summary>
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

    /// <summary>"General: exaggeration 0.5, CFG weight 0.5. Expressive: exaggeration 0.7, CFG weight 0.3."</summary>
    public string Describe() =>
        $"General: exaggeration {Number(GeneralExaggeration)}, CFG weight {Number(GeneralCfgWeight)}. " +
        $"Expressive: exaggeration {Number(ExpressiveExaggeration)}, CFG weight {Number(ExpressiveCfgWeight)}.";

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
}
