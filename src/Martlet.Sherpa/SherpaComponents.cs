using System.Security.Cryptography;

namespace Martlet.Sherpa;

/// <summary>Parakeet download progress: bytes received so far and the total.</summary>
public readonly record struct SherpaProgress(long Received, long Total);

/// <summary>One file Martlet installs, pinned by exact size and SHA-256.</summary>
internal sealed record SherpaFile(string Path, long Bytes, string Sha256);

internal sealed record SherpaDownload(Uri Source, long Bytes, string Sha256, IReadOnlyList<SherpaFile> Files);

/// <summary>Where Martlet's speech components are. Voice recognition is part of Martlet: the sherpa-onnx 1.13.8 runtime with
/// ONNX Runtime (the NuGet package org.k2fsa.sherpa.onnx.runtime.win-x64) and the WeSpeaker and pyannote voice models ship in
/// Martlet's own folder (the build downloads the models at pinned SHA-256). Parakeet (one of <see cref="ParakeetModels"/>) is the
/// one part downloaded later, on the owner's request, into the data folder's speech directory; every file is checked for its
/// exact size and SHA-256 before it is used, and a failed or changed download is deleted, never kept.</summary>
public static class SherpaComponents
{
    public const string Version = "1.13.8";
    /// <summary>The folder in Martlet's own folder with the bundled voice models.</summary>
    public const string VoiceModelsFolder = "voice-recognition";
    internal const string SpeakerModel = "wespeaker_en_voxceleb_resnet34_LM.onnx";
    internal const string SegmentationFolder = "pyannote-segmentation-3-0";
    private static readonly SemaphoreSlim installing = new(1, 1);

    /// <summary>Martlet's own folder (where the running application is).</summary>
    public static string AppDirectory => AppContext.BaseDirectory;

    /// <summary>The folder with the bundled sherpa-onnx runtime: Martlet's folder in an installed build, or
    /// runtimes\win-x64\native in a portable development build. Null when it is missing.</summary>
    public static string? RuntimeDirectory(string? appDirectory = null)
    {
        var app = Path.GetFullPath(appDirectory ?? AppDirectory);
        foreach (var candidate in new[] { app, Path.Combine(app, "runtimes", "win-x64", "native") })
            if (File.Exists(Path.Combine(candidate, "sherpa-onnx-c-api.dll")) && File.Exists(Path.Combine(candidate, "onnxruntime.dll")))
                return candidate;
        return null;
    }

    /// <summary>The bundled WeSpeaker and pyannote voice models.</summary>
    public static string VoiceModelsDirectory(string? appDirectory = null) =>
        Path.Combine(Path.GetFullPath(appDirectory ?? AppDirectory), VoiceModelsFolder);

    internal static string SpeakerModelPath(string models) => Path.Combine(models, SpeakerModel);
    internal static string SegmentationModelPath(string models) => Path.Combine(models, SegmentationFolder, "model.onnx");

    /// <summary>The runtime and both voice models are in Martlet's folder, so voices can be recognized.</summary>
    public static bool VoiceRecognitionIncluded(string? appDirectory = null)
    {
        var models = VoiceModelsDirectory(appDirectory);
        return RuntimeDirectory(appDirectory) is not null && File.Exists(SpeakerModelPath(models)) && File.Exists(SegmentationModelPath(models));
    }

    // ---------- Parakeet (downloaded on request; the models are in ParakeetModels) ----------

    public static string Megabytes(long bytes) => $"{bytes / 1_000_000.0:N0} MB";

    internal static string ParakeetDirectory(string root, ParakeetModel model) => Path.Combine(root, "models", model.Id);

    /// <summary>Every file of <paramref name="model"/> is in <paramref name="root"/> (the data folder's speech directory) with its
    /// pinned size (hashes were checked when it was installed).</summary>
    public static bool IsParakeetInstalled(string root, ParakeetModel model) =>
        model.Downloads.SelectMany(d => d.Files).All(file => IsCurrent(root, file));

    /// <summary>The model with this ID is downloaded in <paramref name="root"/>; false for a model this Martlet doesn't know.</summary>
    public static bool IsParakeetInstalled(string root, string modelId) =>
        ParakeetModels.Find(modelId) is { } model && IsParakeetInstalled(root, model);

    /// <summary>The Parakeet models downloaded in <paramref name="root"/>, fastest first.</summary>
    public static IReadOnlyList<ParakeetModel> InstalledParakeetModels(string root) =>
        [.. ParakeetModels.All.Where(model => IsParakeetInstalled(root, model))];

    /// <summary>Downloads and verifies the missing files of <paramref name="model"/> under <paramref name="root"/> and writes its
    /// NOTICE beside it. Call only after the owner agreed to the download and its size. One download runs at a time.</summary>
    public static async Task InstallParakeetAsync(string root, ParakeetModel model, IProgress<SherpaProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        root = Path.GetFullPath(root);
        await installing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var total = model.DownloadBytes;
            long done = 0;
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Martlet");
            foreach (var download in model.Downloads)
            {
                if (download.Files.All(file => IsCurrent(root, file)))
                {
                    done += download.Bytes;
                    progress?.Report(new(done, total));
                    continue;
                }
                var start = done;
                await DownloadAsync(client, root, download, received => progress?.Report(new(start + received, total)), cancellationToken)
                    .ConfigureAwait(false);
                done += download.Bytes;
            }
            var notices = Path.Combine(root, "models");
            Directory.CreateDirectory(notices);
            await File.WriteAllTextAsync(Path.Combine(notices, model.NoticeFile), model.Notice, cancellationToken).ConfigureAwait(false);
        }
        finally { installing.Release(); }
    }

    private static bool IsCurrent(string root, SherpaFile file) =>
        new FileInfo(Path.Combine(root, file.Path)) is { Exists: true } info && info.Length == file.Bytes;

    private static async Task DownloadAsync(HttpClient client, string root, SherpaDownload download, Action<long> report,
        CancellationToken cancellationToken)
    {
        var folder = Path.Combine(root, "downloads");
        Directory.CreateDirectory(folder);
        var partial = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            using (var response = await client.GetAsync(download.Source, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps ||
                    response.Content.Headers.ContentLength is { } length && length != download.Bytes)
                    throw new InvalidDataException("The download was not the expected file.");
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, true);
                var buffer = new byte[1 << 16];
                long count = 0, reported = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    count += read;
                    if (count > download.Bytes) throw new InvalidDataException("The download was larger than expected.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    if (count - reported >= 1 << 20 || count == download.Bytes)
                    {
                        reported = count;
                        report(count);
                    }
                }
                if (count != download.Bytes) throw new InvalidDataException("The download was cut short.");
            }
            await VerifyAsync(partial, download.Bytes, download.Sha256, cancellationToken).ConfigureAwait(false);
            var file = download.Files.Single();
            var destination = Path.Combine(root, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(partial, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    private static async Task VerifyAsync(string path, long bytes, string sha256, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true);
        if (stream.Length != bytes) throw new InvalidDataException("A downloaded file has an unexpected size.");
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (hash != sha256) throw new InvalidDataException("A downloaded file didn't match what Martlet expected.");
    }
}
