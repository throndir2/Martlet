using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Presentation;
using Martlet.Providers;

namespace Martlet.Mcp;

/// <summary>character_theme: a character model's colors and palettes as Settings › Appearance makes them, with the production
/// code (Martlet.Avatar.Hosting's color reading, rules and Thinking parser; the desktop's texture decoding, picture sheet and
/// window sample, linked from Martlet.Desktop). Optionally parses a simulated Thinking answer, asks Ollama on this PC with
/// the real request and picture (<c>live</c>; loopback only), and writes PNG pictures of Martlet's window in each palette.</summary>
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

    internal static async Task<object> RunAsync(string? modelPath, string? dataDirectory, string? answer, bool live, string? model,
        string? previewDirectory, string? label, string? name, string? about, CancellationToken cancellation)
    {
        if (name is not null && (name.Length is 0 or > 80 || name.Any(char.IsControl))) throw new ArgumentException("name is 1-80 characters on one line.");
        if (about is not null && (about.Length is 0 or > CharacterIdentity.MaximumSource || about.Any(char.IsControl)))
            throw new ArgumentException($"about is 1-{CharacterIdentity.MaximumSource} characters on one line.");
        if (model is not null)
        {
            try { ChatCompletionsSetup.ModelId(model); }
            catch (ContractException error) { throw new ArgumentException(error.Message); }
        }
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
        CharacterIdentity identity;
        try { (id, images, identity) = await CharacterThemeImages.ReadModelAsync(renderer, modelPath, cancellation); }
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
        PromptSettings? prompts = null;
        if (dataDirectory is not null && File.Exists(Path.Combine(dataDirectory, "settings.json")))
            prompts = SettingsJson.Read(File.ReadAllBytes(Path.Combine(dataDirectory, "settings.json"))).Prompts;
        var instructions = CharacterThemePrompt.Instructions(prompts);
        var sheet = await Sta(() => CharacterThemeImages.Sheet(null, images));
        var picture = sheet is null ? CharacterThemePrompt.NoPicture
            : images.Any(i => i.Thumbnail) ? CharacterThemePrompt.Thumbnail : CharacterThemePrompt.Textures;
        var saved = dataDirectory is null ? null : CharacterThemes.Load(dataDirectory, id);
        // Who the character is, as the desktop tells the Thinking model: the list's name (here the model file's, or name) with
        // the name its files give it, and the owner's words (about, or saved) or what its files say about where it is from.
        var listName = name ?? (BundledLive2D.IsBuiltIn(modelPath) ? modelPath[BundledLive2D.Prefix.Length..] : SharedCharacterModels.NameFor(modelPath));
        var who = identity.Name is { } own && !string.Equals(own, listName, StringComparison.OrdinalIgnoreCase) ? $"{listName} ({own})" : listName;
        var from = about ?? saved?.About ?? identity.Source;
        var message = CharacterThemePrompt.Message(swatches, renderer, picture, who, from);
        CharacterThinkingTheme? parsedTheme = null;
        object? parsed = null;
        if (answer is not null)
        {
            var (theme, problem) = CharacterThemePrompt.Parse(answer, picture, DateTimeOffset.Now, swatches);
            parsedTheme = theme;
            parsed = theme is null ? new { read = false, problem } : Thinking(theme);
        }
        object liveResult;
        if (!live) liveResult = new { ran = false, why = "Pass live: true (and model) to ask Ollama on this PC with this request and picture (loopback only; it loads the model)." };
        else if (instructions is null) liveResult = new { ran = false, why = "Companion › Prompts › Character theme colors is empty." };
        else
        {
            var liveModel = model ?? await SavedLocalModelAsync(dataDirectory, cancellation);
            if (liveModel is null) liveResult = new { ran = false, why = "Thinking isn't Ollama on this PC; pass model to name a model Ollama has." };
            else
            {
                var asked = await AskOllamaAsync(liveModel, instructions, message, sheet, cancellation);
                var (theme, problem) = CharacterThemePrompt.Parse(asked.Reply, picture, DateTimeOffset.Now, swatches);
                parsedTheme ??= theme;
                liveResult = new
                {
                    ran = true, model = liveModel, asked.Outcome, asked.Failure, asked.FirstWordsMs, asked.TotalMs,
                    note = "Ollama on this PC over loopback, with the production Chat Completions adapter, instructions, message and picture.",
                    reply = asked.Reply.Length > 4000 ? asked.Reply[..4000] + "…" : asked.Reply,
                    read = theme is not null, problem, theme = theme is null ? null : Thinking(theme)
                };
            }
        }
        object? previews = null;
        if (previewDirectory is not null)
        {
            Directory.CreateDirectory(previewDirectory);
            var prefix = label ?? id[..16];
            var thinking = parsedTheme ?? saved?.Thinking;
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
                if (thinking is not null)
                {
                    Write("thinking-light", thinking.Light, "Character light by Thinking");
                    Write("thinking-dark", thinking.Dark, "Character dark by Thinking");
                }
                if (sheet is not null)
                {
                    var file = $"{prefix}-picture.jpg";
                    File.WriteAllBytes(Path.Combine(previewDirectory, file), sheet.Content.ToArray());
                    files.Add(file);
                }
                return files;
            });
            previews = new { directory = previewDirectory, files = written };
        }
        return new
        {
            renderer = renderer.ToString(), key = id[..16], textures = images.Count(i => !i.Thumbnail), thumbnail = images.Any(i => i.Thumbnail),
            analyzeMs,
            identity = new { fromFiles = identity, told = new { name = who, about = from } },
            swatches = swatches.Select(s => new { s.Hex, share = Math.Round(s.Share, 3), s.Kind, s.Name }),
            sources = new
            {
                tint = sources.Tint?.Hex, tintStrength = Math.Round(sources.TintStrength, 2), accents = sources.Accents.Select(a => a.Hex),
                light = sources.Light?.Hex, darkest = sources.Darkest?.Hex
            },
            rules = new { light = Palette(rulesLight), dark = Palette(rulesDark) },
            request = new
            {
                instructions, message, picture,
                pictureSize = sheet is null ? null : $"{sheet.Width}x{sheet.Height}, {sheet.ByteCount / 1024} KB JPEG"
            },
            saved = saved is null ? null : new { swatches = saved.Swatches.Count, thinking = saved.Thinking is null ? null : Thinking(saved.Thinking) },
            parsed,
            live = liveResult,
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

    private static object Thinking(CharacterThinkingTheme theme) => new
    {
        read = true, light = Palette(theme.Light), dark = Palette(theme.Dark), theme.Choice, theme.Why, theme.Picture, theme.Fixes
    };

    private static async Task<string?> SavedLocalModelAsync(string? dataDirectory, CancellationToken cancellation)
    {
        if (dataDirectory is null) return null;
        var loaded = await new SettingsStore(dataDirectory).LoadAsync(cancellation);
        var thinking = loaded.Settings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        return thinking is not null && ContextBudget.IsLocalOllama(thinking.RouteType, thinking.Origin) ? thinking.ModelId : null;
    }

    private sealed record Asked(string? Outcome, string? Failure, string Reply, long? FirstWordsMs, long TotalMs);

    private static async Task<Asked> AskOllamaAsync(string model, string instructions, string message, BoundedImage? image, CancellationToken cancellation)
    {
        const string baseUrl = GenerationSupport.LocalOllamaChatBaseUrl;
        using (var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) })
        {
            try
            {
                using var version = await client.GetAsync("http://127.0.0.1:11434/api/version", cancellation);
                if (!version.IsSuccessStatusCode) return new(null, $"Ollama on this PC answered {(int)version.StatusCode}.", "", null, 0);
            }
            catch (HttpRequestException) { return new(null, "Ollama isn't running on this PC.", "", null, 0); }
        }
        var limits = new TextGenerationLimits
        {
            MaxOutputTokens = 4096, MaxEvents = 4094, FirstDeltaTimeout = TimeSpan.FromMinutes(2), IdleTimeout = TimeSpan.FromMinutes(2),
            MaxRequestTime = TimeSpan.FromMinutes(2), MaxInputTokens = 32_768, MaxContextTokens = 36_864
        };
        using var adapter = ChatCompletionsTextGenerationAdapter.Create(baseUrl);
        var ids = new CorrelationIds { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };
        var deadline = DateTimeOffset.UtcNow + limits.MaxRequestTime;
        var selection = new TextModelSelection(ChatCompletionsSetup.Alias, model);
        var authorization = new TextDisclosureAuthorization(new(ChatCompletionsSetup.BaseUri(baseUrl), ProviderRole.Llm, model),
            selection, ids, 1, limits, deadline, true, true, allowImageDisclosure: image is not null);
        var clock = Stopwatch.StartNew();
        long? first = null;
        var stream = adapter.Stream(new() { Ids = ids, Epoch = 1, Deadline = deadline }, selection,
            new BoundedTextInput(message, instructions, image: image), limits, authorization, cancellation,
            new GenerationSettings { Reasoning = false });
        var reply = new StringBuilder();
        await foreach (var item in stream.WithCancellation(cancellation))
            if (item.Kind == ProviderEventKind.TextDelta)
            {
                first ??= clock.ElapsedMilliseconds;
                reply.Append(item.Text);
            }
        return new(stream.Result?.Outcome.ToString(), stream.Result?.Failure?.Code.ToString(), reply.ToString().Trim(), first, clock.ElapsedMilliseconds);
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
