using Martlet.Core.Installation;

namespace Martlet.Desktop.Tests;

public sealed class OllamaRecoveryHealthTests
{
    private static readonly OllamaPlaces Places = new(@"C:\Users\owner\AppData\Local\Ollama", @"C:\Users\owner\.ollama\models",
        @"C:\Users\owner\AppData\Local\Programs\Ollama");
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 21, 11, 0, TimeSpan.FromHours(-7));
    private const string Error = "Error: mkdir C:\\Users\\owner\\.ollama\\models: Cannot create a file when that file already exists.: " +
        "ensure path elements are traversable";
    private const string Config = "time=2026-10-08T21:10:44.491-07:00 level=INFO source=routes.go:2373 msg=\"server config\" " +
        "env=\"map[OLLAMA_MODELS:C:\\\\Users\\\\owner\\\\.ollama\\\\models ROCR_VISIBLE_DEVICES:]\"";

    private static OllamaDiagnosis Diagnosis(bool knownShape = true, bool app = true, string error = Error) =>
        OllamaRecovery.Diagnose(new(true, true, app, false, false, OllamaLogs.Parse([Config, error, Config, error], [], Now.AddSeconds(-1)),
            new(true, knownShape, @"C:\Users\owner\.ollama\models", knownShape ? null : "Ollama's app settings have no models column."), null,
            [new(@"C:\Users\owner\.ollama\models", @"C:\Users\owner\.ollama\models", @"E:\Ollama\models", @"E:\Ollama\models", true)], Now), Places);

    [Fact]
    public void OllamaRecovery_Home_keeps_the_plain_item_when_Ollama_says_nothing()
    {
        Assert.Equal(("Ollama isn't running on this PC", "Thinking uses gemma4:e2b on this PC, but Ollama isn't running."),
            MainWindow.OllamaHealthText("gemma4:e2b", null, false, null));
    }

    [Fact]
    public void OllamaRecovery_Home_shows_Ollamas_own_error_and_the_fix_it_makes()
    {
        var trouble = Diagnosis();

        var (title, detail) = MainWindow.OllamaHealthText("gemma4:e2b", trouble, false, null);
        Assert.Equal("Ollama keeps stopping on this PC", title);
        Assert.StartsWith("Thinking uses gemma4:e2b on this PC, but Ollama keeps stopping (2 failed starts in its log). Ollama says: " +
            "\"mkdir C:\\Users\\owner\\.ollama\\models: Cannot create a file", detail, StringComparison.Ordinal);
        Assert.Contains(@"Its models folder C:\Users\owner\.ollama\models is a link to E:\Ollama\models", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Martlet can point", detail, StringComparison.Ordinal);
        Assert.EndsWith(@"Choose Fix Ollama's models folder to point Ollama at E:\Ollama\models.", detail, StringComparison.Ordinal);

        Assert.EndsWith(@"Martlet is pointing Ollama at E:\Ollama\models now...",
            MainWindow.OllamaHealthText("gemma4:e2b", trouble, true, null).Detail, StringComparison.Ordinal);

        var failed = new OllamaRepairResult(false, "Martlet couldn't stop Ollama (access is denied), so it changed nothing.", [], null, null, trouble);
        Assert.EndsWith("Martlet tried to fix it: Martlet couldn't stop Ollama (access is denied), so it changed nothing.",
            MainWindow.OllamaHealthText("gemma4:e2b", trouble, false, failed).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void OllamaRecovery_Home_gives_guidance_when_Martlet_cant_fix_it()
    {
        var (_, unknown) = MainWindow.OllamaHealthText("gemma4:e2b", Diagnosis(knownShape: false), false, null);
        Assert.EndsWith(@"Open Ollama's settings, set its model location to E:\Ollama\models, then start Ollama again. (Martlet can't change " +
            "it itself: Ollama's app settings have no models column.)", unknown, StringComparison.Ordinal);

        var (title, other) = MainWindow.OllamaHealthText("gemma4:e2b",
            Diagnosis(app: false, error: "Error: listen tcp 127.0.0.1:11434: bind: Only one usage of each socket address."), false, null);
        Assert.Equal("Ollama keeps stopping on this PC", title);
        Assert.Contains("Ollama says: \"listen tcp 127.0.0.1:11434: bind: Only one usage of each socket address\".", other, StringComparison.Ordinal);
        Assert.EndsWith($"Its logs are in {Places.DataDirectory}.", other, StringComparison.Ordinal);
    }
}
