using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Martlet.Companion;

/// <summary>Which character to show and the files the renderer may read.</summary>
public sealed record CharacterModel(string Renderer, string Root, string ModelFile, IReadOnlyList<string> Files, string Revision)
{
    public const int MaxFiles = 2_000;

    /// <summary>The character folder shipped with the app (the renderer bundle and the Live2D SDK and sample).</summary>
    public static string BundleFolder => Path.Combine(AppContext.BaseDirectory, "character");

    public static bool BundleBuilt => File.Exists(Path.Combine(BundleFolder, "app.js"));

    /// <summary>A .vrm/.glb file, a Live2D .model3.json, or null for the bundled Live2D sample (Hiyori) when it shipped.</summary>
    public static CharacterModel? Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            var bundled = Path.Combine(BundleFolder, "live2d", "characters", "Hiyori", "Hiyori.model3.json");
            return File.Exists(bundled) ? Live2D(bundled) : null;
        }
        if (!File.Exists(path)) throw new CompanionException($"The character file {path} doesn't exist.");
        if (path.EndsWith(".model3.json", StringComparison.OrdinalIgnoreCase)) return Live2D(path);
        if (path.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
            return new("Vrm", Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileName(path), [Path.GetFileName(path)], NewRevision());
        throw new CompanionException("Choose a VRM (.vrm, .glb) or Live2D (.model3.json) character.");
    }

    private static CharacterModel Live2D(string model)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(model))!;
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => !f.StartsWith('.') && f is not "core.js" and not "sdk.js").Take(MaxFiles + 1).ToList();
        if (files.Count > MaxFiles) throw new CompanionException("That Live2D folder has too many files; choose the model's own folder.");
        return new("Live2D", root, Path.GetFileName(model), files, NewRevision());
    }

    private static string NewRevision() => Guid.NewGuid().ToString("N");

    /// <summary>The renderer's load command (the same message the Windows renderer takes).</summary>
    public object LoadMessage() => new
    {
        kind = "load",
        data = Renderer == "Live2D"
            ? (object)new { renderer = Renderer, assets = Files.Append("core.js").Append("sdk.js").ToArray(), modelFile = ModelFile, resourceRevision = Revision, extras = (object?)null }
            : new { renderer = Renderer, assets = Files.ToArray(), modelFile = ModelFile, resourceRevision = Revision }
    };
}

/// <summary>Serves the character page and only the chosen model's files on 127.0.0.1 under a random path, so the web
/// view reads them without file:// access and other local programs can't guess the address.</summary>
public sealed class CharacterServer : IDisposable
{
    /// <summary>Gives the renderer bundle (written for WebView2's window.chrome.webview) Avalonia NativeWebView's bridge:
    /// messages to the app go through invokeCSharpAction, messages from it arrive through window.__martletSend.</summary>
    internal const string Bridge =
        "(function(){const ls=[];window.chrome={webview:{postMessage:v=>{try{invokeCSharpAction(JSON.stringify(v))}catch(e){}}," +
        "addEventListener:(t,f)=>{if(t==='message')ls.push(f)}}};window.__martletSend=m=>ls.forEach(f=>f({data:m}));})();";

    internal const string Page =
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Martlet</title>" +
        "<style>html,body{margin:0;width:100%;height:100%;background:transparent;overflow:hidden}canvas{width:100%;height:100%;display:block;background:transparent}</style>" +
        "<script>" + Bridge + "</script></head><body><canvas id=\"avatar\"></canvas><script type=\"module\" src=\"app.js\"></script></body></html>";

    private readonly HttpListener listener = new();
    private readonly string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    private readonly string bundle;
    private volatile CharacterModel? model;

    public Uri Address { get; }

    public CharacterServer(string bundleFolder)
    {
        bundle = bundleFolder;
        var port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/{token}/");
        listener.Start();
        Address = new($"http://127.0.0.1:{port}/{token}/index.html");
        _ = Task.Run(ServeAsync);
    }

    public CharacterModel? Model { get => model; set => model = value; }

    /// <summary>How many requests were answered, and the last one: shows whether the web view reached the page.</summary>
    public int Served { get; private set; }
    public string? LastServed { get; private set; }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private async Task ServeAsync()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            try { Answer(context); Served++; LastServed = $"{context.Request.Url?.AbsolutePath.Split('/').LastOrDefault()} {context.Response.StatusCode}"; }
            catch (Exception error) when (error is IOException or HttpListenerException or UnauthorizedAccessException) { }
            finally { try { context.Response.Close(); } catch (Exception error) when (error is HttpListenerException or ObjectDisposedException) { } }
        }
    }

    private void Answer(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "";
        var prefix = $"/{token}/";
        var name = path.StartsWith(prefix, StringComparison.Ordinal) ? Uri.UnescapeDataString(path[prefix.Length..]) : null;
        var file = name is null || context.Request.HttpMethod != "GET" ? null : Resolve(name);
        if (file is null && name == "index.html")
        {
            Send(context, Encoding.UTF8.GetBytes(Page), "text/html; charset=utf-8");
            return;
        }
        if (file is null || !File.Exists(file))
        {
            context.Response.StatusCode = 404;
            return;
        }
        Send(context, File.ReadAllBytes(file), ContentType(file));
    }

    /// <summary>The file for a request name, or null. Only the bundle's app.js, the Live2D SDK and the chosen model's
    /// listed files are served.</summary>
    internal string? Resolve(string name)
    {
        if (name == "app.js") return Path.Combine(bundle, "app.js");
        if (!name.StartsWith("asset/", StringComparison.Ordinal)) return null;
        var asset = name["asset/".Length..];
        if (asset is "core.js" or "sdk.js") return Path.Combine(bundle, "live2d", "sdk", asset);
        var current = model;
        if (current is null || !current.Files.Contains(asset, StringComparer.Ordinal)) return null;
        var full = Path.GetFullPath(Path.Combine(current.Root, asset));
        return full.StartsWith(Path.GetFullPath(current.Root) + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }

    private static string ContentType(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".js" => "text/javascript",
        ".json" => "application/json",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        _ => "application/octet-stream"
    };

    private static void Send(HttpListenerContext context, byte[] body, string type)
    {
        context.Response.ContentType = type;
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.ContentLength64 = body.Length;
        context.Response.OutputStream.Write(body);
    }

    public static string Script(object message) => "window.__martletSend(" + JsonSerializer.Serialize(message) + ")";

    public void Dispose()
    {
        listener.Stop();
        listener.Close();
    }
}
