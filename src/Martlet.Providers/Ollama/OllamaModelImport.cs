using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Martlet.Providers.Ollama;

/// <summary>Use a model file you downloaded: puts GGUF files (from Hugging Face, LM Studio's folder or anywhere else) into an
/// Ollama as a model of its own, the way <c>ollama create</c> does: each file is sent as a blob (<c>POST /api/blobs/sha256:...</c>,
/// skipped when Ollama already has it), then <c>POST /api/create</c> names the model with the files. A projector file
/// (<c>mmproj*.gguf</c>, which lets a model see) and the other parts of a split model come along. Ollama keeps its own copy.</summary>
public static partial class OllamaModelImport
{
    private const int MaxNameLength = 64;

    [GeneratedRegex(@"^(?<stem>.+)-(?<part>\d{5})-of-(?<count>\d{5})$", RegexOptions.CultureInvariant)]
    private static partial Regex SplitPart();

    /// <summary>Whether <paramref name="path"/> names a projector file (<c>mmproj</c> in its name).</summary>
    public static bool IsProjector(string path) =>
        Path.GetFileName(path).Contains("mmproj", StringComparison.OrdinalIgnoreCase);

    /// <summary>The files to import for what the owner picked: only .gguf files; every part of a split model
    /// (<c>name-00001-of-00003.gguf</c>) when one part is picked; and, when no projector is picked and the model's folder has
    /// exactly one, that projector (<paramref name="addedProjector"/>). Models first, the projector last. Throws
    /// <see cref="ArgumentException"/> with what is wrong in plain words.</summary>
    public static IReadOnlyList<string> Files(IEnumerable<string> picked, out string? addedProjector)
    {
        ArgumentNullException.ThrowIfNull(picked);
        addedProjector = null;
        var files = new List<string>();
        foreach (var path in picked.Select(Path.GetFullPath))
        {
            if (!string.Equals(Path.GetExtension(path), ".gguf", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"{Path.GetFileName(path)} isn't a GGUF model file. Ollama takes .gguf files.");
            if (!File.Exists(path)) throw new ArgumentException($"{path} isn't there.");
            var split = SplitPart().Match(Path.GetFileNameWithoutExtension(path));
            if (!split.Success)
            {
                files.Add(path);
                continue;
            }
            var count = int.Parse(split.Groups["count"].Value, CultureInfo.InvariantCulture);
            var folder = Path.GetDirectoryName(path)!;
            for (var part = 1; part <= count; part++)
            {
                var name = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture,
                    $"{split.Groups["stem"].Value}-{part:00000}-of-{count:00000}{Path.GetExtension(path)}"));
                if (!File.Exists(name))
                    throw new ArgumentException($"{Path.GetFileName(path)} is part of a model in {count} files, but {Path.GetFileName(name)} " +
                        "isn't next to it. Download every part first.");
                files.Add(name);
            }
        }
        files = [.. files.Distinct(StringComparer.OrdinalIgnoreCase)];
        if (files.All(IsProjector))
            throw new ArgumentException(files.Count == 0 ? "Choose a GGUF model file." :
                "That is only a projector (mmproj) file. Choose the model's own GGUF file too.");
        var duplicate = files.GroupBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new ArgumentException($"Two files are named {duplicate.Key}. Choose one of them.");
        if (!files.Any(IsProjector))
        {
            var folders = files.Select(f => Path.GetDirectoryName(f)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var projectors = folders.Count == 1
                ? Directory.EnumerateFiles(folders[0], "*.gguf").Where(IsProjector).Take(2).ToList() : [];
            if (projectors.Count == 1) files.Add(addedProjector = projectors[0]);
        }
        return [.. files.Where(f => !IsProjector(f)), .. files.Where(IsProjector)];
    }

    /// <summary>The Ollama model name for a downloaded file: its file name without the extension and the split part, in lower
    /// case, with anything Ollama doesn't take in a name turned into '-' ("Qwen3-8B-Q4_K_M.gguf" is "qwen3-8b-q4_k_m").</summary>
    public static string NameFor(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var stem = Path.GetFileNameWithoutExtension(path);
        if (SplitPart().Match(stem) is { Success: true } split) stem = split.Groups["stem"].Value;
        var name = new StringBuilder(stem.Length);
        foreach (var c in stem.ToLowerInvariant())
        {
            var keep = char.IsAsciiLetterOrDigit(c) || c is '_' or '.' ? c : '-';
            if (keep == '-' && name.Length > 0 && name[^1] == '-') continue;
            name.Append(keep);
        }
        var text = name.ToString().Trim('-', '.', '_');
        if (text.Length > MaxNameLength) text = text[..MaxNameLength].TrimEnd('-', '.', '_');
        return text.Length == 0 ? "my-model" : text;
    }

    /// <summary>The bytes the files take together, for the confirmation (Ollama keeps a copy of each).</summary>
    public static long Bytes(IEnumerable<string> files) => files.Sum(f => new FileInfo(f).Length);

    /// <summary>Puts <paramref name="files"/> into the Ollama at <paramref name="origin"/> as <paramref name="model"/>, reporting
    /// each step and its progress. Throws <see cref="InvalidOperationException"/> with what went wrong, in Ollama's words where it
    /// gave any.</summary>
    public static async Task ImportAsync(HttpClient client, Uri origin, string model, IReadOnlyList<string> files, Action<string> status,
        IProgress<string> output, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(output);
        if (files.Count == 0) throw new InvalidOperationException("No model file was chosen.");
        var digests = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < files.Count; i++)
        {
            var path = files[i];
            var name = Path.GetFileName(path);
            var size = new FileInfo(path).Length;
            var which = files.Count == 1 ? name : $"{name} ({i + 1} of {files.Count})";
            output.Report($"Reading {name} ({Gb(size)}).");
            var digest = await DigestAsync(path, size, percent => status($"Reading {which}: {percent}%"), token).ConfigureAwait(false);
            var blob = new Uri(origin, "api/blobs/" + digest);
            using (var head = new HttpRequestMessage(HttpMethod.Head, blob))
            using (var have = await client.SendAsync(head, token).ConfigureAwait(false))
            {
                if (have.IsSuccessStatusCode)
                {
                    output.Report($"Ollama already has {name}.");
                    digests[name] = digest;
                    continue;
                }
            }
            output.Report($"Copying {name} into Ollama.");
            await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
            {
                var progress = new ProgressStream(file, size, percent => status($"Copying {which} into Ollama: {percent}%"));
                using var content = new StreamContent(progress, 1 << 20);
                content.Headers.ContentLength = size;
                using var push = await client.PostAsync(blob, content, token).ConfigureAwait(false);
                if (!push.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Ollama didn't take {name} (error {(int)push.StatusCode})" +
                        (await Said(push, token).ConfigureAwait(false) is { } said ? $": {said}" : "."));
            }
            digests[name] = digest;
        }

        status($"Making {model} in Ollama...");
        output.Report($"Making {model} in Ollama from {string.Join(", ", digests.Keys)}.");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, "api/create"))
        {
            Content = new StringContent(JsonSerializer.Serialize(new { model, files = digests, stream = true }), Encoding.UTF8, "application/json")
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Ollama couldn't make {model} (error {(int)response.StatusCode})" +
                (await Said(response, token).ConfigureAwait(false) is { } refused ? $": {refused}" : "."));
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? last = null;
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0) continue;
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { output.Report(line); continue; }
            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (root.TryGetProperty("error", out var error))
                    throw new InvalidOperationException($"Ollama couldn't make {model}: {error.GetString()}");
                var text = root.TryGetProperty("status", out var value) ? value.GetString() ?? "" : "";
                if (text.Length > 0 && text != last) output.Report(text);
                last = text;
            }
        }
        if (last != "success") throw new InvalidOperationException($"Ollama didn't finish making {model}. The output has details.");
    }

    /// <summary>The file's SHA-256 as Ollama names blobs (<c>sha256:</c> and 64 lower-case hex digits).</summary>
    public static async Task<string> DigestAsync(string path, long size, Action<int>? percent, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var buffer = new byte[4 << 20];
        long done = 0;
        var shown = -1;
        int read;
        while ((read = await file.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            done += read;
            var now = size > 0 ? (int)Math.Min(100, done * 100 / size) : 100;
            if (now != shown) percent?.Invoke(shown = now);
        }
        return "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string Gb(long bytes) => bytes >= 1_000_000_000
        ? (bytes / 1e9).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
        : (bytes / 1e6).ToString("0", CultureInfo.InvariantCulture) + " MB";

    private static async Task<string?> Said(HttpResponseMessage response, CancellationToken token)
    {
        string body;
        try { body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false); }
        catch (HttpRequestException) { return null; }
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.String)
                return error.GetString();
        }
        catch (JsonException) { }
        body = body.Trim();
        return body.Length == 0 ? null : body.Length > 200 ? body[..200] : body;
    }

    /// <summary>Reads a file for an upload and says how far it got, in whole percent.</summary>
    private sealed class ProgressStream(Stream inner, long size, Action<int> percent) : Stream
    {
        private long done;
        private int shown = -1;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => size;
        public override long Position { get => done; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int Count(int read)
        {
            done += read;
            var now = size > 0 ? (int)Math.Min(100, done * 100 / size) : 100;
            if (now != shown) percent(shown = now);
            return read;
        }
    }
}
