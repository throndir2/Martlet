using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Martlet.Providers.Ollama;

namespace Martlet.Providers.Tests;

public sealed class OllamaModelImportTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("martlet-import-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private string Write(string name, string text = "FIXTURE")
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, text + name, Encoding.ASCII);
        return path;
    }

    [Theory]
    [InlineData("Qwen3-8B-Q4_K_M.gguf", "qwen3-8b-q4_k_m")]
    [InlineData("gemma 3 (4b) it.gguf", "gemma-3-4b-it")]
    [InlineData("Big-Model-00001-of-00003.gguf", "big-model")]
    [InlineData("___.gguf", "my-model")]
    public void The_model_name_comes_from_the_file_name(string file, string name) =>
        Assert.Equal(name, OllamaModelImport.NameFor(Path.Combine("C:\\models", file)));

    [Fact]
    public void A_lone_projector_in_the_folder_comes_along_last()
    {
        var model = Write("gemma-3-4b-it-Q4_K_M.gguf");
        var projector = Write("mmproj-model-f16.gguf");
        var files = OllamaModelImport.Files([model], out var added);
        Assert.Equal([model, projector], files);
        Assert.Equal(projector, added);
    }

    [Fact]
    public void Two_projectors_in_the_folder_mean_none_is_guessed()
    {
        var model = Write("model.gguf");
        Write("mmproj-a.gguf");
        Write("mmproj-b.gguf");
        Assert.Equal([model], OllamaModelImport.Files([model], out var added));
        Assert.Null(added);
    }

    [Fact]
    public void One_part_of_a_split_model_brings_every_part()
    {
        var first = Write("big-00001-of-00002.gguf");
        var second = Write("big-00002-of-00002.gguf");
        Assert.Equal([first, second], OllamaModelImport.Files([second], out _));
        File.Delete(first);
        Assert.Contains("isn't next to it", Assert.Throws<ArgumentException>(() => OllamaModelImport.Files([second], out _)).Message);
    }

    [Fact]
    public void Only_gguf_model_files_are_taken()
    {
        Assert.Contains("isn't a GGUF", Assert.Throws<ArgumentException>(() => OllamaModelImport.Files([Write("notes.txt")], out _)).Message);
        Assert.Contains("only a projector", Assert.Throws<ArgumentException>(() => OllamaModelImport.Files([Write("mmproj.gguf")], out _)).Message);
    }

    [Fact]
    public async Task Import_sends_each_new_blob_with_its_digest_then_creates_the_model()
    {
        var model = Write("model.gguf", "weights");
        var projector = Write("mmproj.gguf", "projector");
        string Digest(string path) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        var handler = new Handler(Digest(projector));
        using var client = new HttpClient(handler);
        var output = new List<string>();
        await OllamaModelImport.ImportAsync(client, new Uri("http://127.0.0.1:11434/"), "my-model", [model, projector], _ => { },
            new Collect(output), CancellationToken.None);

        Assert.Equal(["/api/blobs/" + Digest(model)], handler.Pushed);
        Assert.Contains(output, line => line.Contains("already has mmproj.gguf", StringComparison.Ordinal));
        using var created = JsonDocument.Parse(handler.Created!);
        Assert.Equal("my-model", created.RootElement.GetProperty("model").GetString());
        Assert.Equal(Digest(model), created.RootElement.GetProperty("files").GetProperty("model.gguf").GetString());
        Assert.Equal(Digest(projector), created.RootElement.GetProperty("files").GetProperty("mmproj.gguf").GetString());
    }

    [Fact]
    public async Task Ollamas_own_words_say_why_it_refused_the_model()
    {
        var model = Write("model.gguf");
        using var client = new HttpClient(new Handler(null, "{\"error\":\"unsupported architecture\"}\n"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => OllamaModelImport.ImportAsync(client,
            new Uri("http://127.0.0.1:11434/"), "my-model", [model], _ => { }, new Collect([]), CancellationToken.None));
        Assert.Contains("unsupported architecture", error.Message, StringComparison.Ordinal);
    }

    private sealed class Collect(List<string> lines) : IProgress<string>
    {
        public void Report(string value) => lines.Add(value);
    }

    private sealed class Handler(string? has, string create = "{\"status\":\"parsing GGUF\"}\n{\"status\":\"success\"}\n") : HttpMessageHandler
    {
        internal List<string> Pushed { get; } = [];
        internal string? Created { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Head)
                return new(path == "/api/blobs/" + has ? HttpStatusCode.OK : HttpStatusCode.NotFound);
            if (path.StartsWith("/api/blobs/", StringComparison.Ordinal))
            {
                var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                Assert.Equal(path["/api/blobs/".Length..], "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)));
                Pushed.Add(path);
                return new(HttpStatusCode.Created);
            }
            Created = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent(create) };
        }
    }
}
