using System.Text.Json;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Core.Characters;
using Martlet.Core.Settings;
using Martlet.Core.Sync;
using Xunit.Abstractions;

namespace Martlet.Desktop.Tests;

/// <summary>The per-model settings (colors, eyes, touch zones, emotes and motions) keep as many models as the shared character
/// list holds, and their files have room for that many.</summary>
public sealed class CharacterSettingsCapacityTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero).AddTicks(1_234_567);
    private const double Long = 0.12345678901234568;

    private static string Id(int i) => i.ToString("x64");

    private static CharacterThemeEntry Theme(int i) => new()
    {
        ModelId = Id(i), Analyzer = CharacterColors.AnalyzerVersion,
        Swatches = [.. Enumerable.Range(0, CharacterColors.MaximumSwatches).Select(_ => new CharacterSwatch("#A1B2C3", Long, CharacterSwatch.Neutral))]
    };

    // A measurement from two requests with every part, both pictures and long steps.
    private static CharacterEyeMeasurement Eyes(int i)
    {
        var box = new TouchZoneBox(Long, Long, Long, Long);
        RendererEye Eye(double x) => new(new(x, Long, Long), new(x, Long, Long, Long));
        return new()
        {
            ModelId = Id(i), Left = Eye(-Long), Right = Eye(Long), CheekLeft = new(-Long, Long, Long), CheekRight = new(Long, Long, Long),
            Mouth = new(Long, Long, Long), MeasuredAt = Start.AddSeconds(i), Requests = 2, Boxes = new(box, box, box, box, box, box, box),
            Pictures = [new("eyes.png", "eyes", 768, 768, 1_234_567, "image/png"), new("check.png", "check", 768, 768, 1_234_567, "image/png")],
            Steps = [.. Enumerable.Range(0, 3).Select(s => $"step {s}: " + new string('x', 400))]
        };
    }

    // A model with many zones: 32 zones, each with a long name, 16 drawables, 4 bones, two areas, a full reaction list and a narration.
    private static CharacterTouchZoneSettings Zones(int i)
    {
        var box = new TouchZoneBox(Long, Long, Long, Long);
        string[] Drawables(int count, int zone) => [.. Enumerable.Range(0, count).Select(d => $"D_PART_{zone:D2}_MESH_{d:D3}")];
        return new()
        {
            ModelId = Id(i), DetectedBy = CharacterTouchZoneSettings.ByVision, DetectedAt = Start, Crop = box, Whole = true,
            Zones = [.. Enumerable.Range(0, 32).Select(z => new CharacterTouchZone
            {
                Id = $"zone_{z:D2}", Label = new string('L', CharacterTouchZones.MaximumLabelLength), Box = box, Drawables = Drawables(16, z),
                Bones = ["leftUpperArm", "rightUpperArm", "leftLowerArm", "rightLowerArm"], Follows = new string('F', 32),
                Areas = [.. Enumerable.Range(0, 2).Select(_ => new CharacterTouchZoneArea { Box = box, Drawables = Drawables(8, z), Bones = ["head"], Nodes = ["J_Sec_Hair1_01"] })],
                Reaction = new()
                {
                    Actions = [.. Enumerable.Range(0, CharacterTouchZones.MaximumActions).Select(a => $"gesture:gesture_{a:D2}")],
                    Narration = new string('N', CharacterTouchZones.MaximumNarrationLength)
                }
            })]
        };
    }

    // A model with every Martlet gesture, 30 emotes and motions of its own and Martlet's combos, as Merge makes its settings.
    private static CharacterActionSettings Actions(int i)
    {
        var gestures = CharacterActionInventory.AllGestures.Select(g => new CharacterActionSource(g.Id, CharacterActionKind.Gesture, g.Name, g.Does));
        var own = Enumerable.Range(0, Math.Min(30, CharacterActionInventory.MaximumSources - CharacterActionInventory.AllGestures.Count)).Select(n =>
            n % 3 == 0 ? new CharacterActionSource($"motion:Motion {n:D2}", CharacterActionKind.Motion, $"Motion {n:D2}", "")
                : new CharacterActionSource($"expression:Expression {n:D2}", CharacterActionKind.Expression, $"Expression {n:D2}", ""));
        var inventory = new CharacterActionInventory(Id(i), AvatarRenderer.Live2D, [.. own, .. gestures]);
        var merged = CharacterActions.Merge(inventory, null);
        return merged with { Actions = [.. merged.Actions.Select(a => a with { Use = new string('u', 60) })] };
    }

    [Fact]
    public void Every_store_keeps_as_many_models_as_the_character_list_holds()
    {
        Assert.Equal(CharacterModelLibrary.MaximumModels, CharacterThemes.MaximumModels);
        Assert.Equal(CharacterModelLibrary.MaximumModels, CharacterEyes.MaximumModels);
        Assert.Equal(CharacterModelLibrary.MaximumModels, CharacterTouchZones.MaximumModels);
        Assert.Equal(CharacterModelLibrary.MaximumModels, CharacterActions.MaximumModels);
    }

    [Fact]
    public async Task Settings_of_more_than_32_models_are_all_kept_and_shared()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-capacity-").FullName;
        var other = Directory.CreateTempSubdirectory("martlet-capacity-other-").FullName;
        try
        {
            const int count = 40;
            for (var i = 0; i < count; i++)
            {
                await CharacterThemes.SaveAsync(directory, Theme(i), Start.AddSeconds(i));
                await CharacterEyes.SaveAsync(directory, Eyes(i));
                await CharacterTouchZones.SaveAsync(directory, Zones(i), Start.AddSeconds(i));
                await CharacterActions.SaveAsync(directory, Actions(i), Start.AddSeconds(i));
            }
            Assert.Equal(count, CharacterThemes.LoadAll(directory).Count);
            Assert.Equal(count, CharacterEyes.LoadAll(directory).Count);
            Assert.Equal(count, CharacterTouchZones.LoadAll(directory).Count);
            Assert.Equal(count, CharacterActions.LoadAll(directory).Count);
            Assert.NotNull(CharacterThemes.Load(directory, Id(0)));
            Assert.NotNull(CharacterEyes.Load(directory, Id(0)));
            Assert.NotNull(CharacterTouchZones.Load(directory, Id(0)));
            Assert.NotNull(CharacterActions.Load(directory, Id(0)));
            await CharacterActions.ReplaceAllAsync(other, CharacterActions.Share(directory));
            Assert.Equal(count, CharacterActions.LoadAll(other).Count);
        }
        finally
        {
            Directory.Delete(directory, true);
            Directory.Delete(other, true);
        }
    }

    [Fact]
    public async Task Each_file_has_room_for_every_model_and_the_shared_emote_settings_fit()
    {
        var one = Directory.CreateTempSubdirectory("martlet-capacity-one-").FullName;
        var two = Directory.CreateTempSubdirectory("martlet-capacity-two-").FullName;
        try
        {
            foreach (var (directory, models) in new[] { (one, 1), (two, 2) })
                for (var i = 0; i < models; i++)
                {
                    await CharacterThemes.SaveAsync(directory, Theme(i), Start.AddSeconds(i));
                    await CharacterEyes.SaveAsync(directory, Eyes(i));
                    await CharacterTouchZones.SaveAsync(directory, Zones(i), Start.AddSeconds(i));
                    await CharacterActions.SaveAsync(directory, Actions(i), Start.AddSeconds(i));
                }
            // The size of a file with every model: one model's file and the room each further model takes.
            long All(Func<string, long> size) => size(one) + (CharacterModelLibrary.MaximumModels - 1L) * (size(two) - size(one));
            long Bytes(Func<string, string> path, string directory) => new FileInfo(path(directory)).Length;

            var themes = All(d => Bytes(CharacterThemes.Path, d));
            var eyes = All(d => Bytes(CharacterEyes.Path, d));
            var zones = All(d => Bytes(CharacterTouchZones.Path, d));
            var actions = All(d => Bytes(CharacterActions.Path, d));
            var shared = All(d => System.Text.Encoding.UTF8.GetByteCount(CharacterActions.Share(d)));
            // In the shared settings document the value is a JSON string, so its quotes are escaped.
            var sharedInDocument = All(d => JsonSerializer.SerializeToUtf8Bytes(CharacterActions.Share(d)).Length);
            output.WriteLine($"one model: themes {Bytes(CharacterThemes.Path, one):N0}, eyes {Bytes(CharacterEyes.Path, one):N0}, " +
                $"zones {Bytes(CharacterTouchZones.Path, one):N0}, actions {Bytes(CharacterActions.Path, one):N0} bytes");
            output.WriteLine($"{CharacterModelLibrary.MaximumModels:N0} models: themes {themes:N0} of {CharacterThemes.MaximumBytes:N0}; " +
                $"eyes {eyes:N0} of {CharacterEyes.MaximumBytes:N0}; zones {zones:N0} of {CharacterTouchZones.MaximumListBytes:N0}; " +
                $"actions {actions:N0} of {CharacterActions.MaximumBytes:N0}; shared actions {shared:N0} of {SharedSettings.MaximumValueBytes:N0} " +
                $"({sharedInDocument:N0} in the document of {SharedSettings.MaximumBytes:N0})");
            Assert.True(themes <= CharacterThemes.MaximumBytes);
            Assert.True(eyes <= CharacterEyes.MaximumBytes);
            Assert.True(zones <= CharacterTouchZones.MaximumListBytes);
            Assert.True(actions <= CharacterActions.MaximumBytes);
            Assert.True(shared <= SharedSettings.MaximumValueBytes);
        }
        finally
        {
            Directory.Delete(one, true);
            Directory.Delete(two, true);
        }
    }
}
