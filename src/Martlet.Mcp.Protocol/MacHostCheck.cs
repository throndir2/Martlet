using System.Diagnostics;
using System.IO;
using Martlet.Core.Installation;
using Martlet.Core.Platforms;

namespace Martlet.Mcp;

/// <summary>
/// mac_host_check: what this PC can show of the Mac host (docs/MACOS.md, "Host"). In the .NET SDK image it publishes this
/// checkout's gateway self-contained for osx-arm64 and osx-x64 (the files the Mac app bundles) and runs the gateway's Mac
/// commands where they must refuse (macos-status off a Mac) or only explain (macos-setup help). In-process it checks the
/// platform catalog the desktop uses for a Mac host's machine report: Ollama and whisper allowed, F5 and Audio2Face
/// refused for want of an NVIDIA GPU, roles not managed from the desktop. Running on a Mac (launchd, APFS custody, Metal)
/// is reported NOT RUN. Never pulls; the NuGet cache volume martlet-outside-check-nuget is shared with outside_path_check.
/// </summary>
internal static class MacHostCheck
{
    private const string NuGetVolume = "martlet-outside-check-nuget";

    internal static async Task<object> RunAsync(CancellationToken cancellation)
    {
        var steps = new List<object>();
        var passed = true;
        void Step(string name, bool ok, string detail)
        {
            steps.Add(new { name, ok, detail });
            passed &= ok;
        }

        var mac = PlatformDevice.FromHost("studio", new HostHardware("studio", "https://192.168.1.30:9443", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, "native", "macOS 15.5", "Darwin 24.5.0", "Apple M2 Pro", 12, 32, null, "no",
            [new HostGpu("Apple M2 Pro", "apple", 22528, "Metal")]) { Platform = "macos", OsVersion = "15.5", Architecture = "arm64" });
        var ollama = PlatformCatalog.Check("ollama", PlatformSide.Host, mac);
        var whisper = PlatformCatalog.Check("whisper", PlatformSide.Host, mac);
        var f5 = PlatformCatalog.Check("f5", PlatformSide.Host, mac);
        var face = PlatformCatalog.Check("audio2face", PlatformSide.Host, mac);
        Step("catalog-mac-host", mac.Platform == DevicePlatform.MacOs && ollama.Verdict == PlatformVerdict.Yes &&
            whisper.Verdict == PlatformVerdict.Yes && f5.Verdict == PlatformVerdict.No && face.Verdict == PlatformVerdict.No &&
            !PlatformCatalog.ManagesRolesRemotely(mac),
            $"ollama {ollama.Verdict}, whisper {whisper.Verdict}; f5: {f5.Reason} audio2face: {face.Reason}");

        var (found, _) = await HostSupplyCheck.DockerAsync(["image", "inspect", "--format", "{{.Id}}", OutsidePathCheck.SdkImage], null,
            TimeSpan.FromSeconds(30), cancellation);
        if (found != 0)
            return new
            {
                passed = false, exitCode = 2, steps,
                notRun = $"Docker isn't running here or {OutsidePathCheck.SdkImage} isn't on this PC (never pulled; run: docker pull {OutsidePathCheck.SdkImage})."
            };
        var root = HostSupplyCheck.FindCheckout();
        var archive = Path.Combine(Path.GetTempPath(), "martlet-mac-host-check-" + Guid.NewGuid().ToString("N")[..8] + ".tar.gz");
        try
        {
            await HostSupplyCheck.ArchiveCheckoutAsync(root, archive, cancellation);
            var clock = Stopwatch.StartNew();
            const string script = """
                set -u
                mkdir -p /s && tar -xz -C /s && cd /s/Martlet
                p=src/Martlet.Gateway.Host.Linux/Martlet.Gateway.Host.Linux.csproj
                for rid in osx-arm64 osx-x64; do
                  if dotnet publish "$p" -c Release -r "$rid" --self-contained true -p:UseAppHost=true -o "/tmp/$rid" -nologo -v q >"/tmp/$rid.log" 2>&1 &&
                     [ -f "/tmp/$rid/Martlet.Gateway.Host.Linux" ] && [ -f "/tmp/$rid/libSystem.Native.dylib" ]; then
                    echo "STEP|publish-$rid|1|$(du -sm "/tmp/$rid" | cut -f1) MB, self-contained, executable Martlet.Gateway.Host.Linux"
                  else echo "STEP|publish-$rid|0|$(tail -c 300 "/tmp/$rid.log" | tr '\n|' '  ')"; fi
                done
                dotnet publish "$p" -c Release -o /tmp/fd -p:UseAppHost=false -nologo -v q >/tmp/fd.log 2>&1 || { echo "STEP|publish-any|0|$(tail -c 300 /tmp/fd.log | tr '\n|' '  ')"; exit 0; }
                out=$(dotnet /tmp/fd/Martlet.Gateway.Host.Linux.dll macos-setup help 2>&1); rc=$?
                case "$out" in *launchd*) [ "$rc" = 0 ] && ok=1 || ok=0 ;; *) ok=0 ;; esac
                echo "STEP|macos-setup-help|$ok|exit $rc, explains the launchd agent and native Ollama/whisper.cpp"
                out=$(dotnet /tmp/fd/Martlet.Gateway.Host.Linux.dll macos-status 2>&1); rc=$?
                case "$out" in *macos.unsupported*) [ "$rc" = 4 ] && ok=1 || ok=0 ;; *) ok=0 ;; esac
                echo "STEP|macos-commands-refused-off-a-mac|$ok|exit $rc: $(printf '%s' "$out" | head -c 120 | tr '\n|' '  ')"
                """;
            var (exit, text) = await HostSupplyCheck.ProcessAsync("docker",
                ["run", "--rm", "-i", "--pull", "never", "-v", NuGetVolume + ":/root/.nuget/packages", "-e", "CI=true",
                    "-e", "DOTNET_CLI_TELEMETRY_OPTOUT=1", "-e", "DOTNET_NOLOGO=1", OutsidePathCheck.SdkImage, "bash", "-c", script.Replace("\r", "", StringComparison.Ordinal)],
                async (stream, ct) => { await using var file = File.OpenRead(archive); await file.CopyToAsync(stream, ct); },
                TimeSpan.FromMinutes(20), cancellation);
            var lines = text.Split('\n').Where(l => l.StartsWith("STEP|", StringComparison.Ordinal)).ToArray();
            if (lines.Length == 0) Step("sdk-container", false, $"exit {exit}: " + text[Math.Max(0, text.Length - 300)..]);
            foreach (var parts in lines.Select(l => l.TrimEnd('\r').Split('|', 4)))
                Step(parts[1], parts[2] == "1", parts.Length > 3 ? parts[3] : "");
            steps.Add(new { name = "elapsed", ok = true, detail = $"{clock.Elapsed.TotalSeconds:0} s" });
        }
        finally { File.Delete(archive); }
        return new
        {
            passed, exitCode = passed ? 0 : 1, steps,
            notRun = "Running on a Mac: launchd agents, APFS custody, Metal working set, native Ollama and whisper.cpp relays (no Mac here)."
        };
    }
}
