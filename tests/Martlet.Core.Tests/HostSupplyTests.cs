using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Martlet.Core.Nodes;

namespace Martlet.Core.Tests;

public sealed class HostSupplyTests
{
    private static void Add(TarWriter writer, string name, string text) =>
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "Martlet-1.2.3/" + name)
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text))
        });

    private static string Lock(string packages) => "{\"version\":2,\"dependencies\":{" + packages + "}}";

    private static string Archive(Action<TarWriter> write)
    {
        var path = Path.Combine(Path.GetTempPath(), "Martlet.HostSupply." + Guid.NewGuid().ToString("N") + ".tar.gz");
        using (var file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax))
            write(writer);
        return path;
    }

    [Fact]
    public void Needs_come_from_the_engine_and_the_gateway_projects_lock_files()
    {
        var archive = Archive(writer =>
        {
            Add(writer, HostSupply.EngineFile, "#!/usr/bin/env bash\nHELPER_IMAGE=\"busybox\"\nDOTNET_SDK=\"10.0.401\"\n");
            Add(writer, HostSupply.GatewayProject, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup><ProjectReference Include="..\Martlet.Gateway\Martlet.Gateway.csproj" /></ItemGroup>
                </Project>
                """);
            Add(writer, "src/Martlet.Gateway.Host.Linux/packages.lock.json", "\uFEFF" + Lock("""
                "net10.0": {
                  "Grpc.Core.Api": {"type":"Transitive","resolved":"2.84.0","contentHash":"x"},
                  "martlet.gateway": {"type":"Project"}
                },
                "net10.0/linux-x64": {"Runtime.Only": {"type":"Direct","resolved":"1.0.0"}}
                """));
            Add(writer, "src/Martlet.Gateway/Martlet.Gateway.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup><PackageReference Include="Grpc.Tools" PrivateAssets="all" /></ItemGroup>
                  <ItemGroup><ProjectReference Include="../Martlet.Core/Martlet.Core.csproj" /></ItemGroup>
                </Project>
                """);
            Add(writer, "src/Martlet.Gateway/packages.lock.json", Lock("""
                "net10.0": {"Grpc.Tools": {"type":"Direct","requested":"[2.84.0, )","resolved":"2.84.0"},
                            "Grpc.Core.Api": {"type":"Transitive","resolved":"2.84.0"}}
                """));
            Add(writer, "src/Martlet.Core/Martlet.Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            Add(writer, "src/Martlet.Desktop/packages.lock.json", Lock("\"net10.0\": {\"NAudio\": {\"type\":\"Direct\",\"resolved\":\"2.0.0\"}}"));
        });
        try
        {
            var needs = HostSupply.ReadNeeds(archive);
            Assert.Equal("10.0.401", needs.DotnetSdk);
            Assert.Equal(["nuget/grpc.core.api.2.84.0.nupkg", "nuget/grpc.tools.2.84.0.nupkg"], needs.Packages.Select(p => p.Name));
            Assert.Equal("https://api.nuget.org/v3-flatcontainer/grpc.tools/2.84.0/grpc.tools.2.84.0.nupkg", needs.Packages[1].Url);
        }
        finally { File.Delete(archive); }
    }

    [Fact]
    public void A_project_with_packages_but_no_lock_file_or_an_odd_package_is_refused()
    {
        var unlocked = Archive(writer =>
        {
            Add(writer, HostSupply.EngineFile, "DOTNET_SDK=\"10.0.401\"\n");
            Add(writer, HostSupply.GatewayProject, "<Project><ItemGroup><PackageReference Include=\"X\" /></ItemGroup></Project>");
        });
        var odd = Archive(writer =>
        {
            Add(writer, HostSupply.EngineFile, "DOTNET_SDK=\"10.0.401\"\n");
            Add(writer, HostSupply.GatewayProject, "<Project />");
            Add(writer, "src/Martlet.Gateway.Host.Linux/packages.lock.json", Lock("\"net10.0\": {\"x; rm -rf ~\": {\"resolved\":\"1.0.0\"}}"));
        });
        try
        {
            Assert.Throws<InvalidDataException>(() => HostSupply.ReadNeeds(unlocked));
            Assert.Throws<InvalidDataException>(() => HostSupply.ReadNeeds(odd));
        }
        finally
        {
            File.Delete(unlocked);
            File.Delete(odd);
        }
    }

    [Fact]
    public void The_sdk_download_carries_microsofts_published_hash()
    {
        var hash = new string('a', 128);
        using var releases = JsonDocument.Parse($$"""
            {"releases":[
              {"sdk":{"version":"10.0.400","files":[]},
               "sdks":[{"version":"10.0.401","files":[
                 {"name":"dotnet-sdk-linux-arm64.tar.gz","url":"https://example/arm.tar.gz","hash":"{{hash}}"},
                 {"name":"dotnet-sdk-linux-x64.tar.gz","url":"https://builds.dotnet.microsoft.com/x.tar.gz","hash":"{{hash.ToUpperInvariant()}}"}]}]}]}
            """);
        var sdk = HostSupply.DotnetSdk(releases.RootElement, "10.0.401");
        Assert.Equal(new HostSupplyItem("dotnet-sdk-10.0.401-linux-x64.tar.gz", "https://builds.dotnet.microsoft.com/x.tar.gz", hash), sdk);
        Assert.Null(HostSupply.DotnetSdk(releases.RootElement, "10.0.402"));
        Assert.Equal(new HostSupplyItem("dotnet-sdk-10.0.401-linux-arm64.tar.gz", "https://example/arm.tar.gz", hash),
            HostSupply.DotnetSdk(releases.RootElement, "10.0.401", "linux-arm64"));
    }

    [Fact]
    public void Arm64_hosts_get_the_arm64_sdk_and_older_states_the_x64_one()
    {
        Assert.Equal("linux-arm64", HostSupply.ReadState(["arch aarch64", "sdk 10.0.401"]).SdkRid);
        Assert.Equal("aarch64", HostSupply.ReadState(["arch aarch64"]).Architecture);
        Assert.Equal("linux-x64", HostSupply.ReadState(["arch x86_64"]).SdkRid);
        Assert.Equal("linux-x64", HostSupply.ReadState(["sdk 10.0.401"]).SdkRid);
    }

    [Fact]
    public async Task Host_state_and_the_files_sent_round_trip()
    {
        var sum = new string('b', 128);
        var state = HostSupply.ReadState(["sdk 10.0.401", "source git", $"file {sum} nuget/a.1.0.0.nupkg", "file short x", "noise"]);
        Assert.Equal(["10.0.401"], state.Sdks);
        Assert.Equal("git", state.Source);
        Assert.Equal(sum, Assert.Single(state.Files).Value);

        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "payload");
            using var stream = new MemoryStream();
            await HostSupply.WriteArchiveAsync(stream, [("nuget/a.1.0.0.nupkg", file)], CancellationToken.None);
            stream.Position = 0;
            using var reader = new TarReader(stream);
            var entry = reader.GetNextEntry()!;
            Assert.Equal("nuget/a.1.0.0.nupkg", entry.Name);
            Assert.Equal("payload", new StreamReader(entry.DataStream!).ReadToEnd());
            Assert.Null(reader.GetNextEntry());
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Engine_commands_stop_plainly_and_a_supplied_host_never_touches_git()
    {
        var online = HostCheckout.Command("--yes setup", "MARTLET_HOST_ADDRESS=192.168.1.20 ", "1.2.3", refresh: true, update: false,
            supplied: false, closeStdin: true);
        Assert.StartsWith(HostCheckout.CheckoutTurn + "command -v git", online);
        Assert.Contains("Stopped: git is not installed here", online);
        Assert.Contains("git clone -q --depth 1 https://github.com/throndir2/Martlet.git ~/.cache/martlet/clone </dev/null", online);
        Assert.Contains("Stopped: there is no Martlet engine in ~/Martlet", online);
        Assert.True(online.IndexOf("test -x ~/Martlet/deploy/host/martlet-host", StringComparison.Ordinal) <
            online.IndexOf("MARTLET_HOST_ADDRESS=", StringComparison.Ordinal));
        Assert.EndsWith("MARTLET_HOST_ADDRESS=192.168.1.20 ~/Martlet/deploy/host/martlet-host --yes setup", online);
        // One line without double quotes or percent signs: it also fits a Windows command line for ssh.exe.
        foreach (var text in new[] { online, HostCheckout.InternetProbe })
            Assert.DoesNotContain(text, c => c is '"' or '%' or '\n' or '\r');

        var status = HostCheckout.Command("status", "", "1.2.3", refresh: false, update: false, supplied: false, closeStdin: true);
        Assert.StartsWith(HostCheckout.CheckoutTurn + "if [ ! -x ~/Martlet/deploy/host/martlet-host ]; then command -v git", status);
        Assert.Contains("{ [ -d ~/Martlet/.git ] && git -C ~/Martlet pull --ff-only -q </dev/null; } || true; " + HostCheckout.CheckoutTurnEnd +
            "test -x ~/Martlet/deploy/host/martlet-host", status);

        var supplied = HostCheckout.Command("--yes update", "", "1.2.3", refresh: true, update: true, supplied: true, closeStdin: true);
        Assert.DoesNotContain("git", supplied);
        Assert.EndsWith("MARTLET_SUPPLY=$HOME/.cache/martlet/supply ~/Martlet/deploy/host/martlet-host --yes update", supplied);
    }
}
