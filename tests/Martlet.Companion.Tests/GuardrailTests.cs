using System.Runtime.InteropServices;
using Martlet.Companion.Platform;
using Martlet.Core.Cluster;
using Martlet.Core.Platforms;

namespace Martlet.Companion.Tests;

public sealed class GuardrailTests
{
    private static CompanionGuardrails For(string profile) => new(CompanionStatus.Profiles[profile]);

    private static IReadOnlyList<string> Offered(CompanionGuardrails guardrails, string job) =>
        [.. guardrails.Offered(job).Select(o => o.Id)];

    [Theory]
    [InlineData("linux-x64")]
    [InlineData("linux-nvidia")]
    [InlineData("macos-arm64")]
    [InlineData("macos-x64")]
    public void LinuxAndMacOfferOnlyTheBuiltEnginesTheyCanRun(string profile)
    {
        var guardrails = For(profile);
        Assert.Equal(["openai-llm", "chat-completions"], Offered(guardrails, ClusterJobs.Thinking));
        Assert.Equal(["openai-stt"], Offered(guardrails, ClusterJobs.Listening));
        Assert.Equal(["openai-tts"], Offered(guardrails, ClusterJobs.Speaking));
        Assert.Equal(["loudness-lipsync"], Offered(guardrails, ClusterJobs.LipSync));
    }

    [Theory]
    [InlineData("linux-x64", "windows-speech", "Windows speech runs only on Windows")]
    [InlineData("macos-arm64", "windows-speech", "Windows speech runs only on Windows")]
    [InlineData("linux-nvidia", "windows-voices", "Windows voices exist only on Windows")]
    [InlineData("macos-x64", "windows-voices", "Windows voices exist only on Windows")]
    [InlineData("macos-arm64", "audio2face", "NVIDIA")]
    [InlineData("macos-arm64", "f5", "NVIDIA")]
    [InlineData("macos-x64", "mlx-llm", "Intel processor")]
    [InlineData("macos-x64", "apple-on-device-llm", "Intel processor")]
    [InlineData("linux-x64", "mlx-llm", "Apple silicon")]
    [InlineData("linux-x64", "apple-on-device-llm", "Apple devices")]
    public void ImpossibleEnginesAreNeverOfferedAndSayWhy(string profile, string engine, string reason)
    {
        var option = For(profile).Option(PlatformCatalog.Engine(engine));
        Assert.False(option.Offered);
        Assert.Equal(PlatformVerdict.No, option.Verdict);
        Assert.Contains(reason, option.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AppleSiliconMlxIsPlannedButNotOfferedYet()
    {
        var option = For("macos-arm64").Option(PlatformCatalog.Engine("mlx-llm"));
        Assert.False(option.Offered);
        Assert.Equal(PlatformVerdict.NotYet, option.Verdict);
    }

    [Fact]
    public void LocalModelsOnAnIntelMacCarryTheCpuOnlyWarning()
    {
        Assert.Contains("CPU only", Assert.Single(For("macos-x64").ThinkingWarnings("chat-completions", "http://127.0.0.1:11434/v1")),
            StringComparison.Ordinal);
        Assert.Empty(For("macos-x64").ThinkingWarnings("chat-completions", "https://openrouter.ai/api/v1"));
        Assert.Empty(For("macos-arm64").ThinkingWarnings("chat-completions", "http://127.0.0.1:1234/v1"));
        Assert.Empty(For("linux-nvidia").ThinkingWarnings("chat-completions", "http://127.0.0.1:11434/v1"));
        Assert.Single(For("linux-x64").ThinkingWarnings("chat-completions", "http://127.0.0.1:11434/v1"));
    }

    [Fact]
    public void SettingsFromAWindowsPcAreRefusedWithTheCatalogReasonAndTheCurrentChoiceKept()
    {
        var guardrails = For("macos-arm64");
        var current = CompanionSettingsStore.Defaults(guardrails);
        var json = """{"thinking":"chat-completions","chatModel":"qwen3","listening":"windows-speech","speaking":"f5","lipSync":"audio2face","persona":"Be brief."}""";
        var (settings, refusals) = CompanionSettingsStore.Import(json, guardrails, current);

        Assert.Equal("chat-completions", settings.Thinking);
        Assert.Equal("qwen3", settings.ChatModel);
        Assert.Equal("Be brief.", settings.Persona);
        Assert.Equal(current.Listening, settings.Listening);
        Assert.Equal(current.Speaking, settings.Speaking);
        Assert.Equal(current.LipSync, settings.LipSync);
        Assert.Equal(3, refusals.Count);
        Assert.Contains(refusals, r => r.StartsWith("Listening:", StringComparison.Ordinal) && r.Contains("Windows speech runs only on Windows", StringComparison.Ordinal));
        Assert.Contains(refusals, r => r.StartsWith("Speaking:", StringComparison.Ordinal) && r.Contains("NVIDIA", StringComparison.Ordinal));
        Assert.Contains(refusals, r => r.StartsWith("Lip-sync:", StringComparison.Ordinal) && r.Contains("Audio2Face needs an NVIDIA GPU", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownEnginesAreRefusedToo()
    {
        var guardrails = For("linux-x64");
        var (settings, refusals) = guardrails.Admit(new CompanionSettings { Thinking = "something-new" }, CompanionSettingsStore.Defaults(guardrails));
        Assert.Equal("openai-llm", settings.Thinking);
        Assert.Contains("something-new", Assert.Single(refusals), StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsFileRoundTripsAndIsCheckedOnLoad()
    {
        var folder = Path.Combine(Path.GetTempPath(), "martlet-companion-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CompanionSettingsStore(folder);
            var guardrails = For("linux-x64");
            store.Save(new CompanionSettings { Thinking = "chat-completions", Speaking = "windows-voices", CharacterName = "Wren" });
            var (settings, refusals) = store.Load(guardrails);
            Assert.Equal("chat-completions", settings.Thinking);
            Assert.Equal("Wren", settings.CharacterName);
            Assert.Equal("openai-tts", settings.Speaking);
            Assert.Contains("Windows voices exist only on Windows", Assert.Single(refusals), StringComparison.Ordinal);
            Assert.DoesNotContain("api-key", File.ReadAllText(store.FilePath), StringComparison.OrdinalIgnoreCase);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void MacsDefaultToAPushToTalkKeyThatIsNotAMediaKey()
    {
        Assert.Equal("Control+Alt+T", CompanionSettingsStore.Defaults(For("macos-arm64")).PushToTalkKey);
        Assert.Equal("F8", CompanionSettingsStore.Defaults(For("linux-x64")).PushToTalkKey);
        Assert.Equal(new HotkeyGesture("T", HotkeyModifiers.Control | HotkeyModifiers.Alt), CompanionSettings.ParseGesture("Ctrl+Option+T"));
        Assert.Null(CompanionSettings.ParseGesture("Hyper+T"));
    }

    [Fact]
    public void SimulatedStatusReportsGuardrailsAndImportRefusals()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, """{"listening":"windows-speech"}""");
            using var output = new StringWriter();
            Assert.Equal(0, CompanionStatus.Run(["--status", "--as", "linux-x64", "--import", file], output));
            var text = output.ToString();
            Assert.Contains("\"simulated\": true", text, StringComparison.Ordinal);
            Assert.Contains("Windows speech runs only on Windows", text, StringComparison.Ordinal);
            Assert.Contains("\"refusals\"", text, StringComparison.Ordinal);
            using var bad = new StringWriter();
            Assert.Equal(2, CompanionStatus.Run(["--status", "--as", "beos"], bad));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void WindowsDevRunsKeepTheirOwnArchitecture() =>
        Assert.True(new PlatformInfo { Platform = DevicePlatform.Linux, Architecture = Architecture.Arm64 }.ToDevice().Arm64);
}

public sealed class CompanionHelperTests
{
    [Fact]
    public void LongRepliesAreSplitAtSentencesWithinTheSpeechBound()
    {
        var text = string.Join(" ", Enumerable.Range(1, 60).Select(i => $"This is sentence number {i}."));
        var parts = CompanionConversation.SpeechParts(text, 200).ToList();
        Assert.True(parts.Count > 5);
        Assert.All(parts, p => Assert.True(System.Text.Encoding.UTF8.GetByteCount(p) <= 200));
        Assert.All(parts, p => Assert.EndsWith(".", p, StringComparison.Ordinal));
        Assert.Equal(text.Replace(" ", ""), string.Concat(parts).Replace(" ", ""));
        Assert.Single(CompanionConversation.SpeechParts("Hi there"));
        Assert.All(CompanionConversation.SpeechParts(new string('a', 900), 200), p => Assert.True(p.Length <= 200));
    }

    [Fact]
    public void LoudnessDrivesTheMouthAndPcmRoundTrips()
    {
        Assert.Equal(0, Pcm.Loudness(new float[480]));
        var speech = Enumerable.Range(0, 480).Select(i => 0.1f * MathF.Sin(i / 5f)).ToArray();
        Assert.InRange(Pcm.Loudness(speech), 0.3f, 0.6f);
        Assert.Equal(1, Pcm.Loudness(Enumerable.Repeat(0.9f, 480).ToArray()));
        var bytes = new byte[speech.Length * 2];
        Pcm.ToPcm16(speech, bytes);
        var back = Pcm.ToFloat(bytes);
        Assert.All(speech.Zip(back), pair => Assert.InRange(pair.First - pair.Second, -0.001f, 0.001f));
    }

    [Fact]
    public async Task CharacterServerServesOnlyTheBundleAndTheChosenModel()
    {
        var root = Path.Combine(Path.GetTempPath(), "martlet-character-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "model"));
        File.WriteAllText(Path.Combine(root, "model", "Wren.model3.json"), "{}");
        File.WriteAllText(Path.Combine(root, "secret.txt"), "no");
        try
        {
            using var server = new CharacterServer(Path.Combine(root, "bundle"));
            Assert.Equal("127.0.0.1", server.Address.Host);
            var model = CharacterModel.Resolve(Path.Combine(root, "model", "Wren.model3.json"))!;
            Assert.Equal("Live2D", model.Renderer);
            server.Model = model;
            Assert.NotNull(server.Resolve("asset/Wren.model3.json"));
            Assert.Null(server.Resolve("asset/../secret.txt"));
            Assert.Null(server.Resolve("../secret.txt"));
            Assert.Null(server.Resolve("asset/other.png"));
            Assert.EndsWith(Path.Combine("live2d", "sdk", "core.js"), server.Resolve("asset/core.js"), StringComparison.Ordinal);
            Assert.Throws<CompanionException>(() => CharacterModel.Resolve(Path.Combine(root, "secret.txt")));
            Assert.Contains("invokeCSharpAction", CharacterServer.Page, StringComparison.Ordinal);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var page = await http.GetStringAsync(server.Address);
            Assert.Contains("__martletSend", page, StringComparison.Ordinal);
            Assert.Equal("{}", await http.GetStringAsync(new Uri(server.Address, "asset/Wren.model3.json")));
            Assert.Equal(System.Net.HttpStatusCode.NotFound,
                (await http.GetAsync(new Uri(server.Address, "asset/other.png"))).StatusCode);
        }
        finally { Directory.Delete(root, true); }
    }
}
