using System.Globalization;
using System.Text.Json;
using Martlet.Core.Settings;
using Martlet.Desktop;
using Martlet.Mcp;
using Martlet.Sherpa;

namespace Martlet.Desktop.Tests;

/// <summary>The Parakeet models Companion › Listening offers: their pinned downloads and notices, per-model install checks,
/// choosing one for a recording, and what MCP reports. No model is downloaded or loaded.</summary>
public sealed class ParakeetModelsTests : IDisposable
{
    private readonly DirectoryInfo data = Directory.CreateTempSubdirectory("martlet-parakeet-");
    private string Speech => Path.Combine(data.FullName, "speech");

    [Fact]
    public void The_catalog_matches_the_route_ids_fastest_first()
    {
        Assert.Equal(LocalSpeechSetup.ParakeetModelIds, ParakeetModels.All.Select(m => m.Id));
        Assert.Same(ParakeetModels.Tdt110mEnglish, ParakeetModels.Find(LocalSpeechSetup.Parakeet110mEnglishModelId));
        Assert.Same(ParakeetModels.V2English, ParakeetModels.Find(LocalSpeechSetup.ParakeetV2EnglishModelId));
        Assert.Same(ParakeetModels.V3, ParakeetModels.Find(LocalSpeechSetup.ParakeetV3ModelId));
        Assert.Null(ParakeetModels.Find("parakeet-tdt-1.1b"));
        Assert.Null(ParakeetModels.Find(null));
        Assert.Throws<ArgumentException>(() => ParakeetModels.Get("parakeet-tdt-1.1b"));
        Assert.True(ParakeetModels.Tdt110mEnglish.EnglishOnly);
        Assert.True(ParakeetModels.V2English.EnglishOnly);
        Assert.False(ParakeetModels.V3.EnglishOnly);
    }

    [Fact]
    public void Every_file_is_pinned_to_a_revision_size_and_hash()
    {
        Assert.Equal(477_410_591, ParakeetModels.Tdt110mEnglish.DownloadBytes);
        Assert.Equal(661_190_513, ParakeetModels.V2English.DownloadBytes);
        Assert.Equal(670_478_772, ParakeetModels.V3.DownloadBytes);
        Assert.Equal(["encoder.onnx", "decoder.onnx", "joiner.onnx", "tokens.txt"],
            [ParakeetModels.Tdt110mEnglish.Encoder, ParakeetModels.Tdt110mEnglish.Decoder, ParakeetModels.Tdt110mEnglish.Joiner,
                ParakeetModels.Tdt110mEnglish.Tokens]);
        Assert.Equal("encoder.int8.onnx", ParakeetModels.V2English.Encoder);
        foreach (var model in ParakeetModels.All)
        {
            Assert.Equal(4, model.Downloads.Count);
            Assert.Matches("^[0-9a-f]{40}$", model.Revision);
            foreach (var download in model.Downloads)
            {
                Assert.Equal(Uri.UriSchemeHttps, download.Source.Scheme);
                Assert.Equal("huggingface.co", download.Source.Host);
                Assert.StartsWith($"/{model.Repository}/resolve/{model.Revision}/", download.Source.AbsolutePath, StringComparison.Ordinal);
                Assert.Matches("^[0-9a-f]{64}$", download.Sha256);
                Assert.True(download.Bytes > 0);
                var file = Assert.Single(download.Files);
                Assert.Equal((download.Bytes, download.Sha256), (file.Bytes, file.Sha256));
                Assert.StartsWith($@"models\{model.Id}\", file.Path, StringComparison.Ordinal);
            }
            Assert.Contains("CC BY 4.0", model.Notice);
            Assert.Contains("https://huggingface.co/nvidia/", model.Notice);
            Assert.Contains(model.Repository, model.Notice);
            Assert.Contains("sherpa-onnx", model.Notice);
        }
        Assert.Equal("Parakeet-NOTICE.txt", ParakeetModels.V3.NoticeFile);
        Assert.Equal(3, ParakeetModels.All.Select(m => m.NoticeFile).Distinct().Count());
        Assert.Equal(12, ParakeetModels.All.SelectMany(m => m.Downloads).Select(d => d.Source).Distinct().Count());
    }

    [Fact]
    public void Each_model_is_installed_on_its_own()
    {
        Assert.Empty(SherpaComponents.InstalledParakeetModels(Speech));
        Install(ParakeetModels.Tdt110mEnglish);
        Assert.True(SherpaComponents.IsParakeetInstalled(Speech, ParakeetModels.Tdt110mEnglish));
        Assert.True(SherpaComponents.IsParakeetInstalled(Speech, ParakeetModels.Tdt110mEnglishId));
        Assert.False(SherpaComponents.IsParakeetInstalled(Speech, ParakeetModels.V3));
        Assert.False(SherpaComponents.IsParakeetInstalled(Speech, "parakeet-tdt-1.1b"));
        Assert.Equal([ParakeetModels.Tdt110mEnglish], SherpaComponents.InstalledParakeetModels(Speech));
        Install(ParakeetModels.V3);
        Assert.Equal([ParakeetModels.Tdt110mEnglish, ParakeetModels.V3], SherpaComponents.InstalledParakeetModels(Speech));
        // A file of the wrong size is not the model.
        using (var stream = File.OpenWrite(Path.Combine(Speech, "models", ParakeetModels.V3Id, "tokens.txt"))) stream.SetLength(1);
        Assert.False(SherpaComponents.IsParakeetInstalled(Speech, ParakeetModels.V3));
    }

    [Fact]
    public void Unknown_models_are_refused_before_anything_loads()
    {
        Assert.Throws<ArgumentException>(() => new ParakeetEngine(Speech, "parakeet-tdt-1.1b"));
        Assert.False(ParakeetEngine.Installed(Speech, "parakeet-tdt-1.1b"));
        using var engine = new ParakeetEngine(Speech, ParakeetModels.V2EnglishId);
        Assert.Same(ParakeetModels.V2English, engine.Model);
        var missing = Assert.Throws<SherpaException>(() => engine.Warm());
        Assert.Contains("Parakeet TDT 0.6B v2", missing.Message);
        using var listener = new ParakeetListener(Speech);
        Assert.False(listener.Installed(ParakeetModels.V3Id));
        Assert.Null(listener.Loaded);
        Assert.Throws<InvalidOperationException>(() => { _ = listener.TranscribeAsync("parakeet-tdt-1.1b", new byte[320], CancellationToken.None); });
    }

    [Theory]
    [InlineData("en-US", new[] { ParakeetModels.Tdt110mEnglishId, ParakeetModels.V2EnglishId, ParakeetModels.V3Id }, ParakeetModels.V2EnglishId)]
    [InlineData("en-US", new[] { ParakeetModels.Tdt110mEnglishId, ParakeetModels.V3Id }, ParakeetModels.V3Id)]
    [InlineData("en-US", new[] { ParakeetModels.Tdt110mEnglishId }, ParakeetModels.Tdt110mEnglishId)]
    [InlineData("de-DE", new[] { ParakeetModels.Tdt110mEnglishId, ParakeetModels.V2EnglishId, ParakeetModels.V3Id }, ParakeetModels.V3Id)]
    [InlineData("de-DE", new[] { ParakeetModels.Tdt110mEnglishId, ParakeetModels.V2EnglishId }, ParakeetModels.V2EnglishId)]
    [InlineData("de-DE", new string[0], null)]
    public void Recordings_use_the_most_accurate_downloaded_model(string language, string[] installed, string? expected) =>
        Assert.Equal(expected, RecordingTranscriber.ForRecordings([.. installed.Select(ParakeetModels.Get)],
            CultureInfo.GetCultureInfo(language))?.Id);

    [Fact]
    public void Recordings_use_listening_own_model_when_it_is_downloaded()
    {
        if (SherpaComponents.RuntimeDirectory() is null) return;
        Install(ParakeetModels.Tdt110mEnglish);
        Install(ParakeetModels.V3);
        using var listener = new ParakeetListener(Speech);
        SetupRoute[] Listening(string model) =>
            [.. LocalSpeechSetup.SelectParakeet(SetupSettings.Begin(null), model).Setup!.Routes];
        using (var own = RecordingTranscriber.Choose(Listening(ParakeetModels.Tdt110mEnglishId), listener, Speech, CultureInfo.GetCultureInfo("en-US")))
            Assert.Equal(ParakeetModels.Tdt110mEnglishId, own!.ParakeetModel);
        // Listening's model isn't downloaded here: the most accurate downloaded one fills the words in instead.
        using (var other = RecordingTranscriber.Choose(Listening(ParakeetModels.V2EnglishId), listener, Speech, CultureInfo.GetCultureInfo("en-US")))
            Assert.Equal(ParakeetModels.V3Id, other!.ParakeetModel);
        Assert.Null(RecordingTranscriber.Choose(null, null, Path.Combine(data.FullName, "empty")));
    }

    [Fact]
    public void Mcp_reports_the_models_listening_uses_and_recommends()
    {
        Install(ParakeetModels.Tdt110mEnglish);
        File.WriteAllText(Path.Combine(Speech, "models", ParakeetModels.Tdt110mEnglish.NoticeFile), ParakeetModels.Tdt110mEnglish.Notice);
        var settings = LocalSpeechSetup.SelectParakeet(SetupSettings.Begin(null), ParakeetModels.Tdt110mEnglishId);
        File.WriteAllBytes(Path.Combine(data.FullName, "settings.json"), Martlet.Core.Contracts.ContractJson.Write(settings));

        var status = JsonSerializer.SerializeToElement(ParakeetCheck.Status(data.FullName, Speech));
        var listening = status.GetProperty("listening");
        Assert.Equal("LocalParakeet", listening.GetProperty("route").GetString());
        Assert.Equal(ParakeetModels.Tdt110mEnglishId, listening.GetProperty("parakeetModel").GetString());
        Assert.True(listening.GetProperty("downloaded").GetBoolean());
        Assert.Equal(LocalSpeechSetup.RecommendedParakeetModel(CultureInfo.CurrentUICulture), status.GetProperty("recommended").GetString());
        var models = status.GetProperty("models").EnumerateArray().ToArray();
        Assert.Equal(LocalSpeechSetup.ParakeetModelIds, models.Select(m => m.GetProperty("id").GetString()));
        Assert.Equal([true, false, false], models.Select(m => m.GetProperty("downloaded").GetBoolean()));
        Assert.Equal([true, false, false], models.Select(m => m.GetProperty("notice").GetBoolean()));
        Assert.Equal([true, false, false], models.Select(m => m.GetProperty("inUse").GetBoolean()));
        Assert.Equal([477.0, 661, 670], models.Select(m => m.GetProperty("downloadMb").GetDouble()));

        Assert.Same(ParakeetModels.Tdt110mEnglish, ParakeetCheck.ModelFor(null, data.FullName, Speech));
        Assert.Same(ParakeetModels.V2English, ParakeetCheck.ModelFor(ParakeetModels.V2EnglishId, data.FullName, Speech));
        Assert.Throws<ArgumentException>(() => ParakeetCheck.ModelFor("parakeet-tdt-1.1b", data.FullName, Speech));
        Install(ParakeetModels.V3);
        Assert.Same(ParakeetModels.V3, ParakeetCheck.ModelFor(null, Path.Combine(data.FullName, "no-settings"), Speech));
        Assert.Null(ParakeetCheck.ModelFor(null, data.FullName, Path.Combine(data.FullName, "empty")));
    }

    [Fact]
    public void Another_computers_model_waits_until_it_is_downloaded_here()
    {
        Assert.Null(MainWindow.SharedParakeetWaiting(ParakeetModels.V2EnglishId, _ => true));
        var waiting = MainWindow.SharedParakeetWaiting(ParakeetModels.V2EnglishId, model => model != ParakeetModels.V2EnglishId);
        Assert.StartsWith("Parakeet TDT 0.6B v2 (English) isn't downloaded on this PC yet.", waiting);
        Assert.Contains("Companion › Listening", waiting);
        Assert.Contains("newer Martlet", MainWindow.SharedParakeetWaiting("parakeet-tdt-1.1b", _ => true));
    }

    [Theory]
    [InlineData("Stop, wait, hold on a second.", "Stop. Wait. Hold on a second.", 0, 6)]
    [InlineData("Can you remind me to call my sister?", "Can you remind me to call my sister.", 0, 8)]
    [InlineData("Let's go with that one.", "lets go with the one", 2, 5)]
    [InlineData("Hey, what's the weather like?", "Hey what's weather like today", 2, 5)]
    [InlineData("Hello there.", "", 2, 2)]
    public void Word_errors_ignore_case_and_punctuation(string said, string heard, int errors, int words) =>
        Assert.Equal((errors, words), ParakeetCheck.WordErrors(said, heard));

    // The files only need their pinned sizes; sparse, they take no disk space.
    private void Install(ParakeetModel model)
    {
        foreach (var file in model.Downloads.SelectMany(d => d.Files))
        {
            var path = Path.Combine(Speech, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = File.Create(path);
            DeviceIoControl(stream.SafeFileHandle, SetSparse, 0, 0, 0, 0, out _, 0);
            stream.SetLength(file.Bytes);
        }
    }

    private const uint SetSparse = 0x000900C4;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle file, uint code, nint input, uint inputSize,
        nint output, uint outputSize, out uint returned, nint overlapped);

    public void Dispose() => data.Delete(true);
}
