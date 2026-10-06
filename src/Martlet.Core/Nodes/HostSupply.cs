using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Martlet.Core.Nodes;

/// <summary>A file a host without internet access gets from this PC. <see cref="Name"/> is its path under the host's
/// <see cref="HostSupply.RemoteDirectory"/>; <see cref="Sha512"/> (lower-case hex) is the published hash it must have, when
/// its publisher gives one (the .NET SDK). NuGet checks packages against the gateway's lock files when it restores them.</summary>
public sealed record HostSupplyItem(string Name, string Url, string? Sha512 = null);

/// <summary>What building the native gateway needs: the .NET SDK version the engine checks for and the NuGet packages
/// of the gateway's projects.</summary>
public sealed record HostSupplyNeeds(string DotnetSdk, IReadOnlyList<HostSupplyItem> Packages);

/// <summary>What a host already has: installed .NET SDK versions, the files in its supply directory (name to SHA-512)
/// and where ~/Martlet came from ("git", the SHA-512 of the source archive it was unpacked from, or null).</summary>
public sealed record HostSupplyState(IReadOnlySet<string> Sdks, IReadOnlyDictionary<string, string> Files, string? Source)
{
    /// <summary>The computer's processor as <c>uname -m</c> reports it (x86_64, aarch64), or null from an older state.</summary>
    public string? Architecture { get; init; }

    /// <summary>The .NET runtime identifier of the SDK this computer needs: linux-arm64 on ARM64, otherwise linux-x64.</summary>
    public string SdkRid => Architecture is "aarch64" or "arm64" ? "linux-arm64" : "linux-x64";
}

/// <summary>How a host without internet access reaches a host account: Martlet's SSH runner on the desktop, docker exec in checks.</summary>
public interface IHostSupplyChannel
{
    /// <summary>Runs POSIX sh as the host account and returns its exit code and output lines.</summary>
    Task<(int ExitCode, IReadOnlyList<string> Lines)> RunAsync(string command, CancellationToken token);

    /// <summary>Runs POSIX sh with <paramref name="write"/> streaming its stdin; returns the exit code.</summary>
    Task<int> SendAsync(string command, Func<Stream, CancellationToken, Task> write, CancellationToken token);
}

/// <summary>Opens a download on this PC (HTTPS GET): its content and length when known, or null when the address doesn't
/// exist (HTTP 404). Martlet.Core itself makes no network requests; the desktop passes one built on its HTTP client.</summary>
public delegate Task<HostSupplyDownload?> HostSupplyOpen(string url, CancellationToken token);

/// <summary>An opened download (<see cref="HostSupplyOpen"/>); disposing it ends the request.</summary>
public sealed class HostSupplyDownload(Stream content, long? length, IDisposable? owner = null) : IAsyncDisposable
{
    public Stream Content => content;
    public long? Length => length;

    public async ValueTask DisposeAsync()
    {
        await content.DisposeAsync();
        owner?.Dispose();
    }
}

/// <summary>Files for a native Linux host without internet access (see "Computers without internet" in
/// deploy/host/README.md): Martlet's source, the .NET SDK the engine builds the gateway with and the gateway's NuGet
/// packages, downloaded on this PC and kept under the host account's ~/.cache/martlet/supply.</summary>
public static partial class HostSupply
{
    public const string RemoteDirectory = ".cache/martlet/supply";
    public const string SourceArchive = "martlet-source.tar.gz";
    public const string GatewayProject = "src/Martlet.Gateway.Host.Linux/Martlet.Gateway.Host.Linux.csproj";
    public const string EngineFile = "deploy/host/martlet-host";
    private const string Remote = "$HOME/" + RemoteDirectory;
    private const int MaximumProjectFileBytes = 4 << 20;

    /// <summary>Prints "arch &lt;uname -m&gt;", "sdk &lt;version&gt;" per installed .NET SDK, "source git|&lt;sha512&gt;" for
    /// ~/Martlet and "file &lt;sha512&gt; &lt;name&gt;" per supply file.</summary>
    public const string StateScript =
        "echo arch $(uname -m); " +
        "if [ -x ~/.dotnet/dotnet ]; then ~/.dotnet/dotnet --list-sdks 2>/dev/null | sed -n 's/^\\([0-9][^ ]*\\) .*/sdk \\1/p'; fi; " +
        "if [ -d ~/Martlet/.git ]; then echo source git; elif [ -f ~/Martlet/.martlet-supplied ]; then echo source $(cat ~/Martlet/.martlet-supplied); fi; " +
        "if [ -d " + Remote + " ]; then cd " + Remote + " && find . -type f -exec sha512sum {} + | sed 's|^\\([0-9a-f]*\\)  \\./|file \\1 |'; fi; true";

    /// <summary>Unpacks the tar stream on stdin into the supply directory.</summary>
    public const string ReceiveScript = "mkdir -p " + Remote + " && tar -xf - -C " + Remote;

    /// <summary>Removes supply files not named on stdin (one per line): an installed SDK's archive, an unpacked source.</summary>
    public const string PruneScript =
        "cd " + Remote + " 2>/dev/null || exit 0; cat > .keep; find . -type f ! -name .keep | sed 's|^\\./||' | grep -vxF -f .keep | " +
        "while IFS= read -r f; do rm -f -- \"$f\"; done; rm -f .keep";

    /// <summary>Replaces ~/Martlet with the source archive from this PC (the previous one moves to
    /// ~/.cache/martlet/source.previous) and records the archive's SHA-512 in ~/Martlet/.martlet-supplied.</summary>
    public const string InstallSourceScript =
        "set -e\n" +
        "S=" + Remote + "; W=$HOME/.cache/martlet\n" +
        "sum=$(sha512sum \"$S/" + SourceArchive + "\" | cut -d' ' -f1)\n" +
        "rm -rf \"$W/source.new\"; mkdir -p \"$W/source.new\"\n" +
        "tar -xzf \"$S/" + SourceArchive + "\" --strip-components=1 -C \"$W/source.new\"\n" +
        "test -x \"$W/source.new/" + EngineFile + "\" || { echo 'Stopped: the Martlet source from your PC has no " + EngineFile + ".' >&2; exit 1; }\n" +
        "printf '%s\\n' \"$sum\" > \"$W/source.new/.martlet-supplied\"\n" +
        "if [ -e \"$HOME/Martlet\" ] || [ -L \"$HOME/Martlet\" ]; then rm -rf \"$W/source.previous\"; mv \"$HOME/Martlet\" \"$W/source.previous\"; fi\n" +
        "mv \"$W/source.new\" \"$HOME/Martlet\"\n" +
        "echo 'Unpacked the Martlet source from your PC in ~/Martlet.'\n";

    [GeneratedRegex(@"^DOTNET_SDK=""(?<version>[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,5})""\s*$", RegexOptions.Multiline)]
    private static partial Regex EngineSdkPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z")]
    private static partial Regex PackagePartPattern();

    [GeneratedRegex(@"\A[0-9a-f]{128}\z")]
    private static partial Regex Sha512Pattern();

    /// <summary>GitHub's archives of Martlet's source for <paramref name="version"/>: its release tag, then main (as the
    /// online method falls back to main).</summary>
    public static IReadOnlyList<(string Name, string Url)> SourceUrls(string version) =>
    [
        ($"v{version}", $"https://codeload.github.com/throndir2/Martlet/tar.gz/refs/tags/v{version}"),
        ("main", "https://codeload.github.com/throndir2/Martlet/tar.gz/refs/heads/main")
    ];

    /// <summary>Reads, from a source archive (one top-level folder, like GitHub's), the .NET SDK the engine requires
    /// (DOTNET_SDK in deploy/host/martlet-host) and the NuGet packages in the lock files of the gateway's projects.</summary>
    public static HostSupplyNeeds ReadNeeds(string sourceArchive)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using (var file = File.OpenRead(sourceArchive))
        using (var gzip = new GZipStream(file, CompressionMode.Decompress))
        using (var reader = new TarReader(gzip))
        {
            while (reader.GetNextEntry() is { } entry)
            {
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null) continue;
                var slash = entry.Name.IndexOf('/');
                if (slash < 0) continue;
                var name = entry.Name[(slash + 1)..];
                var wanted = name == EngineFile ||
                    name.StartsWith("src/", StringComparison.Ordinal) && (name.EndsWith(".csproj", StringComparison.Ordinal) ||
                        name.EndsWith("/packages.lock.json", StringComparison.Ordinal));
                if (!wanted || entry.Length > MaximumProjectFileBytes) continue;
                using var copy = new MemoryStream();
                entry.DataStream.CopyTo(copy);
                files[name] = copy.ToArray();
            }
        }
        if (!files.TryGetValue(EngineFile, out var engine))
            throw new InvalidDataException($"Martlet's source has no {EngineFile}.");
        var sdk = EngineSdkPattern().Match(Encoding.UTF8.GetString(engine));
        if (!sdk.Success) throw new InvalidDataException($"{EngineFile} names no .NET SDK version (DOTNET_SDK).");

        var packages = new SortedDictionary<string, HostSupplyItem>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>([GatewayProject]);
        while (queue.TryDequeue(out var project))
        {
            if (!seen.Add(project)) continue;
            if (!files.TryGetValue(project, out var xml)) throw new InvalidDataException($"Martlet's source has no {project}.");
            var document = XDocument.Load(new MemoryStream(xml));
            var folder = project[..project.LastIndexOf('/')];
            foreach (var reference in document.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
                if (reference.Attribute("Include")?.Value is { Length: > 0 } include)
                    queue.Enqueue(Combine(folder, include));
            if (files.TryGetValue(folder + "/packages.lock.json", out var lockFile)) ReadLock(lockFile, packages);
            else if (document.Descendants().Any(e => e.Name.LocalName == "PackageReference"))
                throw new InvalidDataException($"{project} has packages but no packages.lock.json.");
        }
        return new(sdk.Groups["version"].Value, packages.Values.ToList());
    }

    private static string Combine(string folder, string include)
    {
        var parts = new List<string>(folder.Split('/'));
        foreach (var part in include.Replace('\\', '/').Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) throw new InvalidDataException($"Project reference {include} leaves the source.");
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }
        return string.Join('/', parts);
    }

    private static void ReadLock(byte[] bytes, SortedDictionary<string, HostSupplyItem> packages)
    {
        var text = bytes.AsMemory();
        if (bytes is [0xEF, 0xBB, 0xBF, ..]) text = text[3..];
        using var document = JsonDocument.Parse(text);
        foreach (var framework in document.RootElement.GetProperty("dependencies").EnumerateObject())
        {
            // "net10.0/linux-x64" sections are runtime-specific restores the framework-dependent gateway doesn't do.
            if (framework.Name.Contains('/')) continue;
            foreach (var package in framework.Value.EnumerateObject())
            {
                if (package.Value.TryGetProperty("type", out var type) && type.GetString() == "Project") continue;
                var id = package.Name.ToLowerInvariant();
                var version = package.Value.GetProperty("resolved").GetString()?.ToLowerInvariant() ?? "";
                if (!PackagePartPattern().IsMatch(id) || !PackagePartPattern().IsMatch(version))
                    throw new InvalidDataException($"Unexpected package {package.Name} {version} in a lock file.");
                var name = $"nuget/{id}.{version}.nupkg";
                packages[name] = new(name, $"https://api.nuget.org/v3-flatcontainer/{id}/{version}/{id}.{version}.nupkg");
            }
        }
    }

    /// <summary>The <paramref name="rid"/> (linux-x64 or linux-arm64) .NET SDK archive for <paramref name="version"/>, with
    /// the SHA-512 Microsoft publishes for it.</summary>
    public static async Task<HostSupplyItem> DotnetSdkAsync(HostSupplyOpen open, string version, CancellationToken token, string rid = "linux-x64")
    {
        var channel = string.Join('.', version.Split('.').Take(2));
        var url = $"https://builds.dotnet.microsoft.com/dotnet/release-metadata/{channel}/releases.json";
        await using var download = await open(url, token) ?? throw new FileNotFoundException($"{url} was not found.");
        using var document = await JsonDocument.ParseAsync(download.Content, cancellationToken: token);
        return DotnetSdk(document.RootElement, version, rid) ??
            throw new InvalidDataException($"Microsoft's release list has no {rid} download of the .NET SDK {version}.");
    }

    internal static HostSupplyItem? DotnetSdk(JsonElement releases, string version, string rid = "linux-x64")
    {
        if (rid is not ("linux-x64" or "linux-arm64")) throw new ArgumentOutOfRangeException(nameof(rid));
        foreach (var release in releases.GetProperty("releases").EnumerateArray())
        {
            var sdks = release.TryGetProperty("sdks", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().ToList() : [];
            if (release.TryGetProperty("sdk", out var single) && single.ValueKind == JsonValueKind.Object) sdks.Add(single);
            foreach (var sdk in sdks.Where(s => s.TryGetProperty("version", out var v) && v.GetString() == version))
                foreach (var file in sdk.GetProperty("files").EnumerateArray())
                {
                    if (file.GetProperty("name").GetString() != $"dotnet-sdk-{rid}.tar.gz") continue;
                    var url = file.GetProperty("url").GetString() ?? "";
                    var hash = file.GetProperty("hash").GetString()?.ToLowerInvariant() ?? "";
                    if (!url.StartsWith("https://", StringComparison.Ordinal) || !Sha512Pattern().IsMatch(hash)) return null;
                    return new($"dotnet-sdk-{version}-{rid}.tar.gz", url, hash);
                }
        }
        return null;
    }

    /// <summary>Reads <see cref="StateScript"/>'s output.</summary>
    public static HostSupplyState ReadState(IEnumerable<string> lines)
    {
        var sdks = new HashSet<string>(StringComparer.Ordinal);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        string? source = null;
        string? architecture = null;
        foreach (var line in lines)
        {
            var parts = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            switch (parts)
            {
                case ["arch", var arch]: architecture = arch; break;
                case ["sdk", var version]: sdks.Add(version); break;
                case ["source", var where]: source = where; break;
                case ["file", var hash, var name] when Sha512Pattern().IsMatch(hash): files[name] = hash; break;
            }
        }
        return new(sdks, files, source) { Architecture = architecture };
    }

    public static async Task<string> Sha512Async(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA512.HashDataAsync(stream, token));
    }

    /// <summary>Downloads <paramref name="item"/> to <paramref name="path"/> unless a good copy is there already (with
    /// <paramref name="reuse"/>): it must match the published SHA-512, and a package must be a NuGet package.</summary>
    public static async Task<string> FetchAsync(HostSupplyOpen open, HostSupplyItem item, string path, bool reuse, IProgress<string>? progress,
        CancellationToken token)
    {
        if (reuse && File.Exists(path) && await GoodAsync(item, path, token)) return path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var part = path + ".part";
        await using (var download = await open(item.Url, token) ?? throw new FileNotFoundException($"{item.Url} was not found."))
        {
            var total = download.Length;
            await using var target = File.Create(part);
            var buffer = new byte[1 << 16];
            long done = 0;
            var shown = System.Diagnostics.Stopwatch.GetTimestamp();
            int read;
            while ((read = await download.Content.ReadAsync(buffer, token)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), token);
                done += read;
                if (progress is not null && System.Diagnostics.Stopwatch.GetElapsedTime(shown) >= TimeSpan.FromSeconds(5))
                {
                    progress.Report($"  {Path.GetFileName(item.Name)}: {Megabytes(done)}{(total is { } all ? " of " + Megabytes(all) : "")}");
                    shown = System.Diagnostics.Stopwatch.GetTimestamp();
                }
            }
        }
        if (!await GoodAsync(item, part, token))
        {
            File.Delete(part);
            throw new InvalidDataException($"{item.Url} downloaded, but it is not the expected file.");
        }
        File.Move(part, path, overwrite: true);
        return path;
    }

    private static async Task<bool> GoodAsync(HostSupplyItem item, string path, CancellationToken token)
    {
        if (item.Sha512 is { } expected) return await Sha512Async(path, token) == expected;
        if (!item.Name.EndsWith(".nupkg", StringComparison.Ordinal)) return true;
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return zip.Entries.Any(e => !e.FullName.Contains('/') && e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidDataException) { return false; }
    }

    public static string Megabytes(long bytes) => $"{Math.Max(1, (bytes + (1 << 19)) >> 20)} MB";

    /// <summary>Writes the files as a tar stream (what <see cref="ReceiveScript"/> unpacks).</summary>
    public static async Task WriteArchiveAsync(Stream output, IReadOnlyList<(string Name, string Path)> files, CancellationToken token)
    {
        await using (var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true))
            foreach (var (name, path) in files)
            {
                await using var data = File.OpenRead(path);
                var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = data,
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead
                };
                await writer.WriteEntryAsync(entry, token);
            }
        await output.FlushAsync(token);
    }
}

/// <summary>Sets a native Linux host without internet access up to build its gateway: downloads Martlet's source, the
/// .NET SDK and the gateway's NuGet packages on this PC (cached in <paramref name="cacheDirectory"/>), sends what the host
/// lacks over <see cref="IHostSupplyChannel"/>, checks every file arrived intact, unpacks the source in ~/Martlet and
/// removes what is no longer needed. The engine then runs with MARTLET_SUPPLY (<see cref="HostCheckout.Command"/>).</summary>
public sealed class HostSupplier(HostSupplyOpen open, string cacheDirectory, IProgress<string> output)
{
    /// <summary>Downloads Martlet's source for <paramref name="version"/> (its release tag, else main).</summary>
    public async Task<string> SourceAsync(string version, CancellationToken token)
    {
        foreach (var (name, url) in HostSupply.SourceUrls(version))
        {
            output.Report($"Downloading Martlet's source ({name}) on this PC...");
            try
            {
                return await HostSupply.FetchAsync(open, new(HostSupply.SourceArchive, url),
                    Path.Combine(cacheDirectory, "source", name + ".tar.gz"), reuse: name != "main", output, token);
            }
            catch (FileNotFoundException) when (name != "main") { output.Report($"There is no {name} release tag; using main."); }
        }
        throw new InvalidOperationException("Martlet's source could not be downloaded.");
    }

    /// <summary>Supplies the host from <paramref name="sourceArchive"/> (a .tar.gz with one top-level folder).</summary>
    public async Task SupplyAsync(IHostSupplyChannel host, string sourceArchive, CancellationToken token)
    {
        var needs = HostSupply.ReadNeeds(sourceArchive);
        var state = await StateAsync(host, token);
        var sourceSum = await HostSupply.Sha512Async(sourceArchive, token);
        var send = new List<(string Name, string Path, string Sha512)>();
        var keep = new List<string>();
        var installSource = state.Source != sourceSum;
        if (installSource) send.Add((HostSupply.SourceArchive, sourceArchive, sourceSum));
        if (!state.Sdks.Contains(needs.DotnetSdk))
        {
            var sdk = await HostSupply.DotnetSdkAsync(open, needs.DotnetSdk, token, state.SdkRid);
            keep.Add(sdk.Name);
            if (state.Files.GetValueOrDefault(sdk.Name) != sdk.Sha512)
            {
                output.Report($"Downloading the .NET SDK {needs.DotnetSdk} for Linux ({state.SdkRid}) on this PC (about 230 MB, kept for next time)...");
                send.Add((sdk.Name, await HostSupply.FetchAsync(open, sdk, Cached(sdk), reuse: true, output, token), sdk.Sha512!));
            }
        }
        output.Report($"Checking the gateway's {needs.Packages.Count} NuGet packages on this PC...");
        foreach (var package in needs.Packages)
        {
            var path = await HostSupply.FetchAsync(open, package, Cached(package), reuse: true, null, token);
            var sum = await HostSupply.Sha512Async(path, token);
            keep.Add(package.Name);
            if (state.Files.GetValueOrDefault(package.Name) != sum) send.Add((package.Name, path, sum));
        }

        if (send.Count > 0)
        {
            var bytes = send.Sum(f => new FileInfo(f.Path).Length);
            output.Report($"Sending {send.Count} file{(send.Count == 1 ? "" : "s")} ({HostSupply.Megabytes(bytes)}) to this computer over SSH...");
            var files = send.Select(f => (f.Name, f.Path)).ToList();
            var exit = await host.SendAsync(HostSupply.ReceiveScript, (stream, t) => HostSupply.WriteArchiveAsync(stream, files, t), token);
            if (exit != 0) throw new InvalidOperationException($"This computer could not store the files from your PC (exit {exit}). Check its free disk space.");
            var arrived = await StateAsync(host, token);
            foreach (var file in send.Where(f => arrived.Files.GetValueOrDefault(f.Name) != f.Sha512))
                throw new InvalidOperationException($"{file.Name} did not arrive intact. Try again.");
            output.Report("Everything arrived intact.");
        }
        else output.Report("This computer already has the files it needs from your PC.");

        if (installSource)
        {
            var (exit, lines) = await host.RunAsync(HostSupply.InstallSourceScript, token);
            foreach (var line in lines) output.Report(line);
            if (exit != 0) throw new InvalidOperationException($"The Martlet source from your PC could not be unpacked (exit {exit}).");
        }
        var text = string.Concat(keep.Select(name => name + "\n"));
        await host.SendAsync(HostSupply.PruneScript, async (stream, t) => await stream.WriteAsync(Encoding.UTF8.GetBytes(text), t), token);
    }

    private string Cached(HostSupplyItem item) => Path.Combine(cacheDirectory, item.Name.Replace('/', Path.DirectorySeparatorChar));

    private static async Task<HostSupplyState> StateAsync(IHostSupplyChannel host, CancellationToken token)
    {
        var (exit, lines) = await host.RunAsync(HostSupply.StateScript, token);
        if (exit != 0) throw new InvalidOperationException($"Could not read what this computer has (exit {exit}).");
        return HostSupply.ReadState(lines);
    }
}
