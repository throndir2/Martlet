using System.IO;
using System.Net.Http;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Reading;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

/// <summary>The Reading pool: reads go to the computer named in Companion › Reading first, then the owner's other computers that
/// run the Reading role, through the work queue.</summary>
public sealed class ReadingPoolTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 15, 0, 0, TimeSpan.Zero);
    private readonly string directory = Directory.CreateTempSubdirectory("martlet-reading-pool-").FullName;

    public ReadingPoolTests()
    {
        HostRegistry.Save(directory, [Paired("gpu-pc", 11), Paired("m3-host", 13), Paired("friend-pc", 20, friend: true), Paired("m4-host", 14)]);
        var plan = ClusterPlan.Empty
            .Observe("gpu-pc", null, [new() { Kind = "ocr", Model = "ppocrv5-server" }], false, "desk-1", Now)
            .Observe("m3-host", null, [new() { Kind = "ocr", Model = "ppocrv5-mobile" }], false, "desk-1", Now)
            .Observe("m4-host", null, [new() { Kind = "audio2face", Model = "a2f" }], false, "desk-1", Now)
            // A friend's host is never in the owner's plan; here it is, to show it is still left out unless named.
            .Observe("friend-pc", null, [new() { Kind = "ocr", Model = "ppocrv5-mobile" }], false, "desk-1", Now);
        ClusterSync.SavePlan(directory, plan);
    }

    public void Dispose() => Directory.Delete(directory, true);

    private static PairedHost Paired(string id, int octet, bool friend = false) => new()
    {
        Pairing = new AvatarRemoteHost
        {
            Origin = $"https://192.168.1.{octet}:9443/", HostId = id, SpkiFingerprint = "sha256:" + new string('a', 64),
            DeviceId = "desktop-test", CredentialId = new string('B', 22)
        },
        Access = friend ? HostSignInAccess.Friend : null
    };

    private static HostRoute Route(string model) => new(Audio2FaceHostConnection.OcrRouteId, "/martlet/v1/inference/ocr", "ocr", "1",
        "ocr", "ocr-worker", "1", model, "1", new string('0', 64), new string('0', 64), 1, 1, 1, 1, 1, 1, TimeSpan.FromSeconds(15), "none");

    private static Task<(HostRoute, IReadOnlyList<ReadLine>)> Reads(string model, string text) =>
        Task.FromResult((Route(model), (IReadOnlyList<ReadLine>)[new ReadLine(text, 0, 0, 10, 10)]));

    private HostScreenTextReader Reader(string? hostId, HostScreenTextReader.HostRead read, List<string> tried, TimeSpan? wait = null) =>
        new(directory, hostId, new WorkQueue { Retry = TimeSpan.FromMilliseconds(10) }, wait ?? TimeSpan.FromSeconds(2), (host, jpeg, token) =>
        {
            lock (tried) tried.Add(host.HostId);
            return read(host, jpeg, token);
        });

    private static Task<IReadOnlyList<ReadLine>> ReadAsync(HostScreenTextReader reader) =>
        reader.ReadAsync(new byte[8 * 4 * 4], 8, 4, CancellationToken.None);

    [Fact]
    public void Reads_try_the_named_computer_first_then_your_other_reading_computers_and_a_friends_host_only_when_named()
    {
        Assert.Equal(["gpu-pc", "m3-host"], new HostScreenTextReader(directory, "gpu-pc").Targets().Select(h => h.HostId));
        Assert.Equal(["m3-host", "gpu-pc"], new HostScreenTextReader(directory, "m3-host").Targets().Select(h => h.HostId));
        Assert.Equal(["friend-pc", "gpu-pc", "m3-host"], new HostScreenTextReader(directory, "friend-pc").Targets().Select(h => h.HostId));
        // None named: the computers the plan says read.
        Assert.Equal(["gpu-pc", "m3-host"], new HostScreenTextReader(directory, null).Targets().Select(h => h.HostId));
    }

    [Fact]
    public async Task The_named_computer_reads_while_it_is_free_and_nothing_else_is_asked()
    {
        List<string> tried = [];
        using var reader = Reader("gpu-pc", (host, _, _) => Reads("ppocrv5-server", "HEALTH 87"), tried);

        Assert.Equal("HEALTH 87", Assert.Single(await ReadAsync(reader)).Text);
        Assert.Equal(["gpu-pc"], tried);
        Assert.Equal("gpu-pc's Reading role (PP-OCRv5 server)", reader.Engine);
    }

    [Fact]
    public async Task A_busy_or_unanswering_named_computer_passes_the_read_to_the_next_at_once()
    {
        List<string> tried = [];
        using var busy = Reader("gpu-pc", (host, _, _) => host.HostId == "gpu-pc"
            ? Task.FromException<(HostRoute, IReadOnlyList<ReadLine>)>(new Audio2FaceHostException("job.busy", "busy"))
            : Reads("ppocrv5-mobile", "VICTORY"), tried);

        Assert.Equal("VICTORY", Assert.Single(await ReadAsync(busy)).Text);
        Assert.Equal(["gpu-pc", "m3-host"], tried);
        Assert.StartsWith("m3-host's Reading role", busy.Engine, StringComparison.Ordinal);

        tried.Clear();
        using var gone = Reader("gpu-pc", (host, _, _) => host.HostId == "gpu-pc"
            ? Task.FromException<(HostRoute, IReadOnlyList<ReadLine>)>(new HttpRequestException("refused"))
            : Reads("ppocrv5-mobile", "VICTORY"), tried);
        Assert.Equal("VICTORY", Assert.Single(await ReadAsync(gone)).Text);
        Assert.Equal(["gpu-pc", "m3-host"], tried);
    }

    [Fact]
    public async Task Every_computer_busy_gives_up_as_busy_after_the_wait()
    {
        List<string> tried = [];
        using var reader = Reader("gpu-pc", (_, _, _) =>
            Task.FromException<(HostRoute, IReadOnlyList<ReadLine>)>(new Audio2FaceHostException("job.busy", "busy")), tried, TimeSpan.FromMilliseconds(80));

        var error = await Assert.ThrowsAsync<ScreenReadException>(() => ReadAsync(reader));
        Assert.True(error.Busy);
        Assert.Contains("Every computer that runs the Reading role is busy", error.Message, StringComparison.Ordinal);
        Assert.Contains("m3-host", tried);
        Assert.True(tried.Count > 2, "it tried them again while it waited");
    }

    [Fact]
    public async Task A_friends_host_busy_with_its_owners_work_hands_the_read_to_your_own_computer()
    {
        List<string> tried = [];
        using var reader = Reader("friend-pc", (host, _, _) => host.HostId == "friend-pc"
            ? Task.FromException<(HostRoute, IReadOnlyList<ReadLine>)>(new Audio2FaceHostException("job.busy", "owner") { Detail = Audio2FaceHostException.OwnerDetail })
            : Reads("ppocrv5-server", "Score 12450"), tried);

        Assert.Equal("Score 12450", Assert.Single(await ReadAsync(reader)).Text);
        Assert.Equal(["friend-pc", "gpu-pc"], tried);
    }

    [Fact]
    public async Task A_read_error_from_the_role_is_reported_and_not_passed_on()
    {
        List<string> tried = [];
        using var reader = Reader("gpu-pc", (_, _, _) =>
            Task.FromException<(HostRoute, IReadOnlyList<ReadLine>)>(new Audio2FaceHostException("request.invalid", "not a picture")), tried);

        var error = await Assert.ThrowsAsync<ScreenReadException>(() => ReadAsync(reader));
        Assert.False(error.Busy);
        Assert.Equal("gpu-pc's Reading role: not a picture", error.Message);
        Assert.Equal(["gpu-pc"], tried);
    }

    [Fact]
    public void Mcp_reading_status_gives_the_order_the_desktop_tries()
    {
        Assert.True(new ReadingSettings { Place = ReadingPlace.Host, HostId = "friend-pc" }.Save(directory));
        var status = System.Text.Json.JsonSerializer.SerializeToElement(
            Martlet.Mcp.ReadingPoolCheck.Status(directory, ReadingSettings.Load(directory), "desk-1"));

        Assert.True(status.GetProperty("pooled").GetBoolean());
        Assert.True(status.GetProperty("chosenIsFriends").GetBoolean());
        Assert.Equal(new HostScreenTextReader(directory, "friend-pc").Targets().Select(h => h.HostId),
            status.GetProperty("tries").EnumerateArray().Select(e => e.GetString()));
    }
}
