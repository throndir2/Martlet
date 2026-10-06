using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Core.Contracts;
using Martlet.Presentation;

namespace Martlet.Mcp;

/// <summary>character_theme: a character model's colors and palettes as Settings › Appearance makes them, with the production
/// code (Martlet.Avatar.Hosting's color reading and rules; the desktop's texture decoding and window sample, linked from
/// Martlet.Desktop). Optionally writes PNG pictures of Martlet's window in each palette.</summary>
internal static class CharacterThemeCheck
{
    private static readonly Uri[] Styles = PackStyles();

    private static Uri[] PackStyles()
    {
        // Registers the pack: scheme, which an app's Application does in Martlet.Desktop.
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        return
        [
            new("pack://application:,,,/Martlet.Mcp.Protocol;component/Themes/Controls.xaml"),
            new("pack://application:,,,/Martlet.Mcp.Protocol;component/Themes/Motion.xaml")
        ];
    }

    internal static async Task<object> RunAsync(string? modelPath, string? dataDirectory, string? previewDirectory, string? label,
        CancellationToken cancellation)
    {
        if (previewDirectory is not null && !Path.IsPathFullyQualified(previewDirectory))
            throw new ArgumentException("previewDirectory must be an absolute folder.");
        if (label is not null && (label.Length is 0 or > 40 || !label.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException("label is 1-40 letters, digits, - or _.");
        var renderer = AvatarRenderer.Live2D;
        if (modelPath is null && dataDirectory is not null)
        {
            try
            {
                using var avatar = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dataDirectory, "avatar.json")));
                modelPath = avatar.RootElement.TryGetProperty("model_path", out var value) ? value.GetString() : null;
                if (avatar.RootElement.TryGetProperty("renderer", out var kind) && kind.ToString().Contains("vrm", StringComparison.OrdinalIgnoreCase))
                    renderer = AvatarRenderer.Vrm;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        }
        modelPath ??= BundledLive2D.Prefix + BundledLive2D.DefaultCharacter;
        if (modelPath.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase)) renderer = AvatarRenderer.Vrm;
        string id;
        IReadOnlyList<CharacterImage> images;
        try { (id, images) = await CharacterThemeImages.ReadModelAsync(renderer, modelPath, cancellation); }
        catch (Exception error) when (error is ContractException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("The model can't be read: " + error.Message);
        }
        var clock = Stopwatch.StartNew();
        var swatches = await Task.Run(() => CharacterThemeImages.Analyze(images), cancellation);
        var analyzeMs = clock.ElapsedMilliseconds;
        if (swatches.Count == 0) throw new InvalidOperationException("No colors were found in the model's textures.");
        var sources = CharacterThemeRules.Pick(swatches);
        var rulesLight = CharacterThemeRules.Build(swatches, dark: false);
        var rulesDark = CharacterThemeRules.Build(swatches, dark: true);
        var saved = dataDirectory is null ? null : CharacterThemes.Load(dataDirectory, id);
        object? previews = null;
        if (previewDirectory is not null)
        {
            Directory.CreateDirectory(previewDirectory);
            var prefix = label ?? id[..16];
            var written = await Sta(() =>
            {
                var files = new List<string>();
                void Write(string name, ThemePalette palette, string title)
                {
                    var file = $"{prefix}-{name}.png";
                    File.WriteAllBytes(Path.Combine(previewDirectory, file), ThemeSample.Png(ThemeSample.Render(palette.Colors, palette.Dark, title, Styles)));
                    files.Add(file);
                }
                Write("rules-light", rulesLight, "Character light");
                Write("rules-dark", rulesDark, "Character dark");
                return files;
            });
            previews = new { directory = previewDirectory, files = written };
        }
        return new
        {
            renderer = renderer.ToString(), key = id[..16], textures = images.Count(i => !i.Thumbnail), thumbnail = images.Any(i => i.Thumbnail),
            analyzeMs,
            swatches = swatches.Select(s => new { s.Hex, share = Math.Round(s.Share, 3), s.Kind, s.Name }),
            sources = new
            {
                tint = sources.Tint?.Hex, tintStrength = Math.Round(sources.TintStrength, 2), accents = sources.Accents.Select(a => a.Hex),
                light = sources.Light?.Hex, darkest = sources.Darkest?.Hex
            },
            rules = new { light = Palette(rulesLight), dark = Palette(rulesDark) },
            saved = saved is null ? null : new { swatches = saved.Swatches.Count },
            previews
        };
    }

    private static object Palette(ThemePalette palette)
    {
        var checks = CharacterThemeRules.Check(palette);
        double Lowest(string role) => checks.Where(c => c.Role == role).Min(c => c.Ratio);
        return new
        {
            colors = palette.Colors,
            problems = CharacterThemeRules.Problems(palette),
            lowestContrast = new
            {
                text = Lowest(ThemeRoles.Text), muted = Lowest(ThemeRoles.Muted), accent = Lowest(ThemeRoles.Accent),
                onAccent = Lowest(ThemeRoles.OnAccent), success = Lowest(ThemeRoles.Success), warning = Lowest(ThemeRoles.Warning),
                border = Lowest(ThemeRoles.Border), focus = Lowest(ThemeRoles.Focus)
            }
        };
    }

    /// <summary>Runs WPF drawing on one long-lived STA thread whose dispatcher keeps running, as in an app.</summary>
    private static Task<T> Sta<T>(Func<T> work) => Wpf.Value.InvokeAsync(work).Task;

    private static readonly Lazy<System.Windows.Threading.Dispatcher> Wpf = new(() =>
    {
        var ready = new TaskCompletionSource<System.Windows.Threading.Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
            ready.SetResult(System.Windows.Threading.Dispatcher.CurrentDispatcher);
            System.Windows.Threading.Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    });
}
