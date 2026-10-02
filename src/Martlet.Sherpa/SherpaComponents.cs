using System.IO.Compression;
using System.Security.Cryptography;

namespace Martlet.Sherpa;

/// <summary>What Martlet downloads to recognize voices and transcribe speech on this PC.</summary>
public enum SherpaPart
{
    /// <summary>The sherpa-onnx 1.13.8 native library with ONNX Runtime (from the official NuGet runtime package).</summary>
    Runtime,
    /// <summary>The WeSpeaker ResNet34-LM speaker embedding model and the pyannote 3.0 speaker segmentation model.</summary>
    Speakers,
    /// <summary>NVIDIA Parakeet TDT 0.6B v3, int8 ONNX export for the processor.</summary>
    Parakeet
}

/// <summary>Download progress: bytes of this part received so far and its total.</summary>
public readonly record struct SherpaProgress(SherpaPart Part, long Received, long Total);

/// <summary>One file Martlet installs, pinned by exact size and SHA-256. <see cref="Entry"/> names a file inside a
/// downloaded zip (the NuGet package) instead of the download itself.</summary>
internal sealed record SherpaFile(string Path, long Bytes, string Sha256, string? Entry = null);

internal sealed record SherpaDownload(Uri Source, long Bytes, string Sha256, IReadOnlyList<SherpaFile> Files);

/// <summary>The pinned downloads behind each part and where they go under Martlet's speech folder. Nothing is downloaded
/// unless the owner asks; every file is checked for its exact size and SHA-256 before it is used, and a failed or changed
/// download is deleted, never kept.</summary>
public static class SherpaComponents
{
    public const string Version = "1.13.8";
    internal const string RuntimeFolder = "sherpa-onnx-" + Version;
    internal const string SpeakerModel = "wespeaker_en_voxceleb_resnet34_LM.onnx";
    internal const string SegmentationFolder = "pyannote-segmentation-3-0";
    public const string ParakeetFolder = "parakeet-tdt-0.6b-v3-int8";
    public const string ParakeetModelId = "parakeet-tdt-0.6b-v3-int8";
    private const string ParakeetRevision = "2bda32ec70b097a55adaa07d9a7173915b43cc78";
    private const string SegmentationRevision = "9403a6902bb58e3d5ae8c7e77c3422de279db2e0";
    private static readonly SemaphoreSlim installing = new(1, 1);

    internal static IReadOnlyList<SherpaDownload> Downloads(SherpaPart part) => part switch
    {
        SherpaPart.Runtime =>
        [
            new(new("https://api.nuget.org/v3-flatcontainer/org.k2fsa.sherpa.onnx.runtime.win-x64/1.13.8/org.k2fsa.sherpa.onnx.runtime.win-x64.1.13.8.nupkg"),
                8_535_869, "2257c06893d1d90792ea3157606de33a51122d935b6913bed5442d2ffb9e9bd7",
            [
                new($@"runtime\{RuntimeFolder}\sherpa-onnx-c-api.dll", 4_605_952, "2729a0da3fbd20fb4e14e157f7cc0e00af848b55f04121319d445c138aeba214",
                    "runtimes/win-x64/native/sherpa-onnx-c-api.dll"),
                new($@"runtime\{RuntimeFolder}\onnxruntime.dll", 17_799_168, "7f66f939a881baf4f46a2216496798edf4a1429878b646d12674aa62f27d8a25",
                    "runtimes/win-x64/native/onnxruntime.dll")
            ])
        ],
        SherpaPart.Speakers =>
        [
            new(new("https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/wespeaker_en_voxceleb_resnet34_LM.onnx"),
                26_530_550, "e9848563da86f263117134dfd7ad63c92355b37de492b55e325400c9d9c39012",
                [new($@"models\{SpeakerModel}", 26_530_550, "e9848563da86f263117134dfd7ad63c92355b37de492b55e325400c9d9c39012")]),
            Hugging("csukuangfj/sherpa-onnx-pyannote-segmentation-3-0", SegmentationRevision, "model.onnx", $@"models\{SegmentationFolder}\model.onnx",
                5_992_913, "220ad67ca923bef2fa91f2390c786097bf305bceb5e261d4af67b38e938e1079"),
            Hugging("csukuangfj/sherpa-onnx-pyannote-segmentation-3-0", SegmentationRevision, "LICENSE", $@"models\{SegmentationFolder}\LICENSE",
                1_061, "14d7016ad68e7394d6e6b78d96cc2ae431c905287b89674cfdf021e79e62b8ba")
        ],
        SherpaPart.Parakeet =>
        [
            Hugging("csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8", ParakeetRevision, "encoder.int8.onnx",
                $@"models\{ParakeetFolder}\encoder.int8.onnx", 652_184_281, "acfc2b4456377e15d04f0243af540b7fe7c992f8d898d751cf134c3a55fd2247"),
            Hugging("csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8", ParakeetRevision, "decoder.int8.onnx",
                $@"models\{ParakeetFolder}\decoder.int8.onnx", 11_845_275, "179e50c43d1a9de79c8a24149a2f9bac6eb5981823f2a2ed88d655b24248db4e"),
            Hugging("csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8", ParakeetRevision, "joiner.int8.onnx",
                $@"models\{ParakeetFolder}\joiner.int8.onnx", 6_355_277, "3164c13fc2821009440d20fcb5fdc78bff28b4db2f8d0f0b329101719c0948b3"),
            Hugging("csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8", ParakeetRevision, "tokens.txt",
                $@"models\{ParakeetFolder}\tokens.txt", 93_939, "d58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d")
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(part))
    };

    private static SherpaDownload Hugging(string repository, string revision, string file, string path, long bytes, string sha256) =>
        new(new($"https://huggingface.co/{repository}/resolve/{revision}/{file}"), bytes, sha256, [new(path, bytes, sha256)]);

    /// <summary>The bytes downloaded to install <paramref name="part"/> (not counting the runtime it needs).</summary>
    public static long DownloadBytes(SherpaPart part) => Downloads(part).Sum(d => d.Bytes);

    /// <summary>What the owner is told before a part is downloaded: what it is, its source and its license.</summary>
    public static string Disclosure(SherpaPart part) => part switch
    {
        SherpaPart.Runtime => $"sherpa-onnx {Version} (Apache-2.0) with ONNX Runtime (MIT), from the official NuGet package " +
            $"org.k2fsa.sherpa.onnx.runtime.win-x64 ({Megabytes(DownloadBytes(SherpaPart.Runtime))})",
        SherpaPart.Speakers => "the WeSpeaker ResNet34-LM voice model (CC BY 4.0, WeSpeaker authors, sherpa-onnx export from GitHub) and the " +
            $"pyannote segmentation 3.0 model (MIT, CNRS, from Hugging Face) ({Megabytes(DownloadBytes(SherpaPart.Speakers))})",
        _ => "NVIDIA Parakeet TDT 0.6B v3 (CC BY 4.0, NVIDIA; int8 ONNX export by the sherpa-onnx project, from Hugging Face) " +
            $"({Megabytes(DownloadBytes(SherpaPart.Parakeet))})"
    };

    public static string Megabytes(long bytes) => $"{bytes / 1_000_000.0:N0} MB";

    public static string RuntimeDirectory(string root) => Path.Combine(root, "runtime", RuntimeFolder);
    internal static string SpeakerModelPath(string root) => Path.Combine(root, "models", SpeakerModel);
    internal static string SegmentationModelPath(string root) => Path.Combine(root, "models", SegmentationFolder, "model.onnx");
    internal static string ParakeetDirectory(string root) => Path.Combine(root, "models", ParakeetFolder);

    /// <summary>Every file of the part is present with its pinned size (hashes were checked when it was installed).</summary>
    public static bool IsInstalled(string root, SherpaPart part) => Downloads(part).SelectMany(d => d.Files)
        .All(file => new FileInfo(Path.Combine(root, file.Path)) is { Exists: true } info && info.Length == file.Bytes);

    /// <summary>Downloads and verifies the missing files of <paramref name="part"/> under <paramref name="root"/>. Call only
    /// after the owner agreed to the <see cref="Disclosure"/> and size.</summary>
    public static async Task InstallAsync(string root, SherpaPart part, IProgress<SherpaProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        root = Path.GetFullPath(root);
        await installing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var total = DownloadBytes(part);
            long done = 0;
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Martlet");
            foreach (var download in Downloads(part))
            {
                if (download.Files.All(file => IsCurrent(root, file)))
                {
                    done += download.Bytes;
                    progress?.Report(new(part, done, total));
                    continue;
                }
                var start = done;
                await DownloadAsync(client, root, download, received => progress?.Report(new(part, start + received, total)), cancellationToken)
                    .ConfigureAwait(false);
                done += download.Bytes;
            }
            var notices = part == SherpaPart.Runtime ? RuntimeDirectory(root) : Path.Combine(root, "models");
            Directory.CreateDirectory(notices);
            await File.WriteAllTextAsync(Path.Combine(notices, part + "-NOTICE.txt"), Notice(part), cancellationToken).ConfigureAwait(false);
        }
        finally { installing.Release(); }
    }

    /// <summary>Deletes a part's files (the voice list itself is kept elsewhere).</summary>
    public static void Remove(string root, SherpaPart part)
    {
        foreach (var file in Downloads(part).SelectMany(d => d.Files))
        {
            var path = Path.Combine(root, file.Path);
            if (File.Exists(path)) File.Delete(path);
        }
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
                    throw new InvalidDataException($"The download from {download.Source.Host} is not the expected file.");
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, true);
                var buffer = new byte[1 << 16];
                long count = 0, reported = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    count += read;
                    if (count > download.Bytes) throw new InvalidDataException($"The download from {download.Source.Host} is larger than expected.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    if (count - reported >= 1 << 20 || count == download.Bytes)
                    {
                        reported = count;
                        report(count);
                    }
                }
                if (count != download.Bytes) throw new InvalidDataException($"The download from {download.Source.Host} was cut short.");
            }
            await VerifyAsync(partial, download.Bytes, download.Sha256, cancellationToken).ConfigureAwait(false);
            if (download.Files is [{ Entry: null } single])
            {
                Place(partial, Path.Combine(root, single.Path));
                return;
            }
            using var archive = ZipFile.OpenRead(partial);
            foreach (var file in download.Files)
            {
                var entry = archive.GetEntry(file.Entry!) ?? throw new InvalidDataException($"The package has no {file.Entry}.");
                if (entry.Length != file.Bytes) throw new InvalidDataException($"{file.Entry} in the package has an unexpected size.");
                var staged = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".partial");
                try
                {
                    await using (var source = entry.Open())
                    await using (var target = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, true))
                        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                    await VerifyAsync(staged, file.Bytes, file.Sha256, cancellationToken).ConfigureAwait(false);
                    Place(staged, Path.Combine(root, file.Path));
                }
                finally
                {
                    if (File.Exists(staged)) File.Delete(staged);
                }
            }
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    private static void Place(string verified, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(verified, destination, overwrite: true);
    }

    private static async Task VerifyAsync(string path, long bytes, string sha256, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true);
        if (stream.Length != bytes) throw new InvalidDataException("A downloaded file has an unexpected size.");
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (hash != sha256) throw new InvalidDataException("A downloaded file did not match its pinned SHA-256 and was deleted.");
    }

    private static string Notice(SherpaPart part) => part switch
    {
        SherpaPart.Runtime =>
            "sherpa-onnx 1.13.8 (https://github.com/k2-fsa/sherpa-onnx), Copyright Xiaomi Corporation and the Next-gen Kaldi team.\n" +
            "License: Apache License 2.0, https://www.apache.org/licenses/LICENSE-2.0\n" +
            "ONNX Runtime (https://github.com/microsoft/onnxruntime), Copyright Microsoft Corporation. License: MIT.\n" +
            "Both come unmodified from the NuGet package org.k2fsa.sherpa.onnx.runtime.win-x64 1.13.8.\n",
        SherpaPart.Speakers =>
            "WeSpeaker ResNet34-LM speaker embedding model (VoxCeleb), by the WeSpeaker authors, https://github.com/wenet-e2e/wespeaker\n" +
            "License: Creative Commons Attribution 4.0 International (CC BY 4.0), https://creativecommons.org/licenses/by/4.0/\n" +
            "ONNX conversion by the sherpa-onnx contributors: " + Downloads(SherpaPart.Speakers)[0].Source + "\n" +
            "The model bytes are unmodified.\n\n" +
            "pyannote segmentation 3.0 (https://huggingface.co/pyannote/segmentation-3.0), Copyright (c) 2023 CNRS, MIT License.\n" +
            "ONNX export from https://huggingface.co/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0 (MIT, Copyright (c) 2022 CNRS; " +
            "see pyannote-segmentation-3-0\\LICENSE).\n",
        _ =>
            "NVIDIA Parakeet TDT 0.6B v3, https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3\n" +
            "License: Creative Commons Attribution 4.0 International (CC BY 4.0), https://creativecommons.org/licenses/by/4.0/\n" +
            "Converted to int8 ONNX by the sherpa-onnx project (Apache-2.0): https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8\n"
    };
}
