using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Martlet.Providers.Ollama;

namespace Martlet.Mcp;

/// <summary>ollama_model_file: Companion › Thinking › This PC › Use a model file you downloaded, headless. With paths (GGUF files
/// the owner would pick) it says what Martlet would add to Ollama: the files (every part of a split model, and a lone mmproj
/// projector in the same folder), the model name and the size; it reads only the folder listing and file sizes and contacts
/// nothing. With fixture=true it runs the production import (<see cref="OllamaModelImport"/>) on FIXTURE files (small text
/// files, NOT real models) against a fixture Ollama on 127.0.0.1 that checks each blob's SHA-256 and the create request, plus
/// the refusals (not a GGUF, only a projector, a missing part) and an Ollama that refuses the model. Saves nothing.</summary>
internal static class OllamaModelFileCheck
{
    internal static async Task<object> RunAsync(IReadOnlyList<string>? paths, bool fixture, CancellationToken cancellation)
    {
        object? plan = null;
        if (paths is { Count: > 0 })
        {
            try
            {
                var files = OllamaModelImport.Files(paths, out var projector);
                plan = new
                {
                    model = OllamaModelImport.NameFor(files[0]), files = files.Select(Path.GetFileName).ToArray(),
                    addedProjector = projector is null ? null : Path.GetFileName(projector), bytes = OllamaModelImport.Bytes(files)
                };
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
            {
                plan = new { refused = error.Message };
            }
        }
        return new { plan, fixture = fixture ? await FixtureAsync(cancellation) : null };
    }

    private static async Task<object> FixtureAsync(CancellationToken cancellation)
    {
        var folder = Path.Combine(Path.GetTempPath(), "martlet-ollama-model-file-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            string Write(string name, string text)
            {
                var path = Path.Combine(folder, name);
                File.WriteAllText(path, text, Encoding.ASCII);
                return path;
            }
            var part1 = Write("Fixture-Model-Q4_K_M-00001-of-00002.gguf", "FIXTURE part one, not a model");
            var part2 = Write("Fixture-Model-Q4_K_M-00002-of-00002.gguf", "FIXTURE part two, not a model");
            var projector = Write("mmproj-fixture-f16.gguf", "FIXTURE projector, not a model");
            var text = Write("notes.txt", "FIXTURE notes");
            string Digest(string path) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            var have = Digest(part2);

            string? createBody = null;
            var blobsChecked = new List<string>();
            await using var ollama = new LocalModelServersCheck.Fixture(request =>
            {
                if (request is { Method: "POST", Path: "/api/create" } && !request.Body.Contains("refuse-me", StringComparison.Ordinal))
                    createBody = request.Body;
                return (request.Method, request.Path) switch
                {
                    ("HEAD", var p) when p.StartsWith("/api/blobs/", StringComparison.Ordinal) =>
                        p == "/api/blobs/" + have ? (200, "text/plain", "") : (404, "text/plain", ""),
                    ("POST", var p) when p.StartsWith("/api/blobs/", StringComparison.Ordinal) =>
                        Check(p["/api/blobs/".Length..], request.Body, blobsChecked),
                    ("POST", "/api/create") when request.Body.Contains("refuse-me", StringComparison.Ordinal) =>
                        (200, "application/x-ndjson", "{\"status\":\"parsing GGUF\"}\n{\"error\":\"unsupported architecture\"}\n"),
                    ("POST", "/api/create") =>
                        (200, "application/x-ndjson", "{\"status\":\"parsing GGUF\"}\n{\"status\":\"writing manifest\"}\n{\"status\":\"success\"}\n"),
                    _ => (404, "application/json", """{"error":"not found"}""")
                };
            });
            var origin = new Uri($"http://127.0.0.1:{ollama.Port}/");

            var files = OllamaModelImport.Files([part1], out var added);
            var model = OllamaModelImport.NameFor(files[0]);
            var lines = new List<string>();
            var statuses = new List<string>();
            string? failure = null;
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            try
            {
                await OllamaModelImport.ImportAsync(client, origin, model, files, s => { lock (statuses) statuses.Add(s); }, new Collect(lines), cancellation);
            }
            catch (InvalidOperationException error) { failure = error.Message; }

            string? sentModel = null;
            Dictionary<string, string>? sentFiles = null;
            if (createBody is not null)
            {
                using var sent = JsonDocument.Parse(createBody);
                sentModel = sent.RootElement.GetProperty("model").GetString();
                sentFiles = sent.RootElement.GetProperty("files").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
            }
            var expected = new Dictionary<string, string>
            {
                [Path.GetFileName(part1)] = Digest(part1), [Path.GetFileName(part2)] = have, [Path.GetFileName(projector)] = Digest(projector)
            };
            var posts = ollama.Requests.Count(r => r.Method == "POST" && r.Path.StartsWith("/api/blobs/", StringComparison.Ordinal));
            string[] firstChecked;
            lock (blobsChecked) firstChecked = [.. blobsChecked];

            string? refused = null;
            try { await OllamaModelImport.ImportAsync(client, origin, "refuse-me", [part1], _ => { }, new Collect([]), cancellation); }
            catch (InvalidOperationException error) { refused = error.Message; }

            string? Refusal(IEnumerable<string> picked)
            {
                try { OllamaModelImport.Files(picked, out _); return null; }
                catch (ArgumentException error) { return error.Message; }
            }
            var notGguf = Refusal([text]);
            var onlyProjector = Refusal([projector]);
            File.Delete(part2);
            var missingPart = Refusal([part1]);

            var ok = failure is null && model == "fixture-model-q4_k_m" && added == projector && files.Count == 3 &&
                sentModel == model && sentFiles is not null && expected.All(e => sentFiles.GetValueOrDefault(e.Key) == e.Value) &&
                sentFiles.Count == 3 && posts == 2 && firstChecked.Length == 2 && firstChecked.All(b => b == "matched") &&
                lines.Any(l => l.Contains("already has", StringComparison.Ordinal)) &&
                refused?.Contains("unsupported architecture", StringComparison.Ordinal) == true &&
                notGguf is not null && onlyProjector is not null && missingPart is not null;
            return new
            {
                ok,
                picked = Path.GetFileName(part1),
                model,
                files = files.Select(Path.GetFileName).ToArray(),
                addedProjector = added is null ? null : Path.GetFileName(added),
                import = new { failure, blobsSent = posts, blobsChecked = firstChecked, createdModel = sentModel, createdFiles = sentFiles, output = lines, lastStatus = statuses.LastOrDefault() },
                ollamaRefuses = refused,
                refusals = new { notGguf, onlyProjector, missingPart }
            };
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static (int, string, string) Check(string digest, string body, List<string> checkedBlobs)
    {
        var matched = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))) == digest;
        lock (checkedBlobs) checkedBlobs.Add(matched ? "matched" : "digest differs");
        return matched ? (201, "text/plain", "") : (400, "application/json", """{"error":"digest mismatch"}""");
    }

    private sealed class Collect(List<string> lines) : IProgress<string>
    {
        public void Report(string value) { lock (lines) lines.Add(value); }
    }
}
