using Martlet.Core.Installation;

namespace Martlet.Core.Tests;

public sealed class SharedGpuTests
{
    private static HostHardware Report(string os, string? kernel, string? platform = "linux") =>
        new("host", "https://192.168.1.45:9443", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "docker", os, kernel, null, null, null,
            "Docker 29.8.1", "yes", []) { Platform = platform };

    [Fact]
    public void Windows_hosts_are_this_pc_docker_desktop_or_a_windows_report()
    {
        Assert.True(SharedGpu.OnWindows(thisPc: true, null));
        Assert.True(SharedGpu.OnWindows(false, Report("Docker Desktop", "5.15.167.4-microsoft-standard-WSL2")));
        Assert.True(SharedGpu.OnWindows(false, Report("Windows 11", null, "windows")));
        Assert.False(SharedGpu.OnWindows(false, Report("Ubuntu 24.04", "6.8.0-45-generic")));
        Assert.False(SharedGpu.OnWindows(false, null));
    }

    [Fact]
    public void The_warning_names_the_voice_and_everything_else_on_the_card()
    {
        string[] roles = ["chatterbox", "audio2face", "stt", "f5"];
        Assert.Equal("Chatterbox Turbo", SharedGpu.VoiceName(roles));
        // Other voice engines have their own warning (Companion › Voice › SpeakingEngineOthers).
        Assert.Equal(["Lip-sync", "Listening"], SharedGpu.Neighbours(roles, thinkingInOllamaHere: false));
        Assert.Equal(["Thinking in Ollama"], SharedGpu.Neighbours(["f5"], thinkingInOllamaHere: true));
        Assert.Empty(SharedGpu.Neighbours(null, false));
        // Reading uses the card only with PP-OCRv5; RapidOCR (or an unknown model) runs on the processor.
        Assert.Equal(["Listening"], SharedGpu.Neighbours(["stt", "ocr"], false));
        Assert.Equal(["Listening"], SharedGpu.Neighbours(["stt", "ocr"], false, new Dictionary<string, string> { ["ocr"] = "rapidocr-ppocrv4" }));
        Assert.Equal(["Listening", "Reading"], SharedGpu.Neighbours(["stt", "ocr"], false, new Dictionary<string, string> { ["ocr"] = "ppocrv5-mobile" }));

        Assert.Equal("diva-host runs on Windows, so Chatterbox Turbo shares its graphics card with Lip-sync and Listening. When the " +
            "card's memory runs short, Windows quietly moves part of it into main memory instead of failing, and the voice can then " +
            "start seconds late or pause mid-sentence. For a steady voice, give it a card of its own: move those jobs or the voice " +
            "to another computer.", SharedGpu.Warning("diva-host", true, "Chatterbox Turbo", ["Lip-sync", "Listening"]));
        Assert.EndsWith("move that job or the voice to another computer.", SharedGpu.Warning("This PC", true, "F5-TTS", ["Singing"]));
        Assert.StartsWith("This PC runs on Windows and already uses its graphics card for Lip-sync, Listening and Singing. A voice " +
            "engine there would share it.", SharedGpu.Warning("This PC", true, null, ["Lip-sync", "Listening", "Singing"]));
        Assert.Null(SharedGpu.Warning("linux-host", false, "Chatterbox Turbo", ["Lip-sync"]));
        Assert.Null(SharedGpu.Warning("diva-host", true, "Chatterbox Turbo", []));
    }
}
