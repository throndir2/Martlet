using System.IO;
using System.Net.Http;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Reading;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

/// <summary>The Reading list: reads go to its places in order (Windows OCR on this PC and your computers' Reading role) through
/// the work queue; an empty list (or nothing on) is off; until the page saves one, the list is made from reading.json.</summary>
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
            .Observe("m4-host", null, [new() { Kind = "audio2face", Model = "a2f" }], false, "desk-1", Now);
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

    private void Save(params PoolMember[] members)
    {
        Assert.True(PoolSettings.SaveFor(directory, PoolAreas.Reading, new PoolList { Area = PoolAreas.Reading.Id, Members = members }));
        WorkSharingRoster.Forget();
    }

    private static HostRoute Route(string model) => new(Audio2FaceHostConnection.OcrRouteId, "/martlet/v1/inference/ocr", "ocr", "1",
        "ocr", "ocr-worker", "1", model, "1", new string('0', 64), new string('0', 64), 1, 1, 1, 1, 1, 1, TimeSpan.FromSeconds(15), "none");

    private static Task<(HostRoute, IReadOnlyList<ReadLine>)> Reads(string model, string text) =>
        Task.FromResult((Route(model), (IReadOnlyList<ReadLine>)[new ReadLine(text, 0, 0, 10, 10)]));

    private static Task<(HostRoute, IReadOnlyList<ReadLine>)> Refuses(string code, string? detail = null) =>
        Task.FromException<(HostRoute, IReadOnlyList<ReadLine>)>(new Audio2FaceHostException(code, code) { Detail = detail });

    private PoolScreenTextReader Reader(PoolScreenTextReader.HostRead read, List<string> tried, TimeSpan? wait = null,
        PoolScreenTextReader.LocalRead? local = null) =>
        new(directory, new WorkQueue { Retry = TimeSpan.FromMilliseconds(10) }, wait ?? TimeSpan.FromSeconds(2), (host, jpeg, token) =>
        {
            lock (tried) tried.Add(host.HostId);
            return read(host, jpeg, token);
        }, (bgra, width, height, token) =>
        {
            lock (tried) tried.Add("windows");
            return local?.Invoke(bgra, width, height, token) ?? Task.FromResult((IReadOnlyList<ReadLine>)[new ReadLine("WINDOWS", 0, 0, 10, 10)]);
        });

    private static Task<IReadOnlyList<ReadLine>> ReadAsync(PoolScreenTextReader reader) =>
        reader.ReadAsync(new byte[8 * 4 * 4], 8, 4, CancellationToken.None);

    [Fact]
    public void The_list_is_made_once_from_the_older_choice_and_an_empty_list_is_off()
    {
        // Nothing chosen: Windows OCR on this PC.
        Assert.Equal(["this-pc"], ReadingList.Load(directory).Members.Select(m => m.Key));
        Assert.True(ReadingPool.UsesWindows(ReadingList.Load(directory).Members[0]));

        // The Reading role on gpu-pc: gpu-pc first, then the other computers the plan says read (a friend's host never).
        Assert.True(new ReadingSettings { Place = ReadingPlace.Host, HostId = "gpu-pc" }.Save(directory));
        Assert.Equal(["host:gpu-pc", "host:m3-host"], ReadingList.Load(directory).Members.Select(m => m.Key));
        Assert.Equal("gpu-pc's Reading role", ScreenReader.For(directory)!.Engine);

        // Saved once by the page; reading.json no longer matters.
        Assert.True(ReadingList.Ensure(directory));
        Assert.True(new ReadingSettings { Place = ReadingPlace.Off }.Save(directory));
        Assert.Equal(["host:gpu-pc", "host:m3-host"], ReadingList.Load(directory).Members.Select(m => m.Key));

        Save(ReadingPool.Windows() with { Off = true });
        Assert.Null(ScreenReader.For(directory));
        Save();
        Assert.Null(ScreenReader.For(directory));
    }

    [Fact]
    public void Reads_try_the_list_in_order_with_each_computer_once_and_skip_what_this_pc_cannot_use()
    {
        Save(ReadingPool.Windows(), PoolMember.Computer("gpu-pc"), PoolMember.Gpu("gpu-pc", 2), PoolMember.Computer("nas-host"),
            PoolMember.Computer("m3-host") with { Off = true }, PoolMember.Computer("friend-pc"));

        var targets = ReadingList.Targets(directory);
        Assert.Equal(["this-pc", "host:gpu-pc", "host:friend-pc"], targets.Select(t => t.Target.Key));
        Assert.Null(targets[0].Host);
        Assert.Equal("gpu-pc", targets[1].Host!.HostId);
    }

    [Fact]
    public async Task The_first_free_place_reads_and_nothing_else_is_asked()
    {
        Save(PoolMember.Computer("gpu-pc"), ReadingPool.Windows());
        List<string> tried = [];
        using var reader = Reader((host, _, _) => Reads("ppocrv5-server", "HEALTH 87"), tried);

        Assert.Equal("gpu-pc's Reading role", reader.Engine);
        Assert.Equal("HEALTH 87", Assert.Single(await ReadAsync(reader)).Text);
        Assert.Equal(["gpu-pc"], tried);
        Assert.Equal("gpu-pc's Reading role (PP-OCRv5 server)", reader.Engine);
    }

    [Fact]
    public async Task A_busy_or_unanswering_computer_passes_the_read_to_the_next_at_once()
    {
        Save(PoolMember.Computer("gpu-pc"), PoolMember.Computer("m3-host"), ReadingPool.Windows());
        List<string> tried = [];
        using var busy = Reader((host, _, _) => host.HostId == "gpu-pc" ? Refuses("job.busy") : Reads("ppocrv5-mobile", "VICTORY"), tried);

        Assert.Equal("VICTORY", Assert.Single(await ReadAsync(busy)).Text);
        Assert.Equal(["gpu-pc", "m3-host"], tried);
        Assert.StartsWith("m3-host's Reading role", busy.Engine, StringComparison.Ordinal);

        tried.Clear();
        using var gone = Reader((host, _, _) => Task.FromException<(HostRoute, IReadOnlyList<ReadLine>)>(new HttpRequestException("refused")), tried);
        Assert.Equal("WINDOWS", Assert.Single(await ReadAsync(gone)).Text);
        Assert.Equal(["gpu-pc", "m3-host", "windows"], tried);
        Assert.Equal("Windows OCR on this PC", gone.Engine);
    }

    [Fact]
    public async Task Windows_without_an_ocr_language_hands_the_read_to_the_next_place()
    {
        Save(ReadingPool.Windows(), PoolMember.Computer("m3-host"));
        List<string> tried = [];
        using var reader = Reader((_, _, _) => Reads("ppocrv5-mobile", "Score 12450"), tried,
            local: (_, _, _, _) => Task.FromException<IReadOnlyList<ReadLine>>(new ScreenReadException(WindowsScreenTextReader.NoLanguage)));

        Assert.Equal("Score 12450", Assert.Single(await ReadAsync(reader)).Text);
        Assert.Equal(["windows", "m3-host"], tried);
    }

    [Fact]
    public async Task Every_place_busy_gives_up_as_busy_after_the_wait()
    {
        Save(PoolMember.Computer("gpu-pc"), PoolMember.Computer("m3-host"));
        List<string> tried = [];
        using var reader = Reader((_, _, _) => Refuses("job.busy"), tried, TimeSpan.FromMilliseconds(80));

        var error = await Assert.ThrowsAsync<ScreenReadException>(() => ReadAsync(reader));
        Assert.True(error.Busy);
        Assert.Contains("Every place in the Reading list is busy", error.Message, StringComparison.Ordinal);
        Assert.True(tried.Count > 2, "it tried them again while it waited");
    }

    [Fact]
    public async Task A_friends_host_busy_with_its_owners_work_hands_the_read_to_your_own_computer()
    {
        Save(PoolMember.Computer("friend-pc"), PoolMember.Computer("gpu-pc"));
        List<string> tried = [];
        using var reader = Reader((host, _, _) => host.HostId == "friend-pc" ? Refuses("job.busy", Audio2FaceHostException.OwnerDetail)
            : Reads("ppocrv5-server", "Score 12450"), tried);

        Assert.Equal("Score 12450", Assert.Single(await ReadAsync(reader)).Text);
        Assert.Equal(["friend-pc", "gpu-pc"], tried);
    }

    [Fact]
    public async Task A_read_error_from_the_role_is_reported_and_not_passed_on()
    {
        Save(PoolMember.Computer("gpu-pc"), ReadingPool.Windows());
        List<string> tried = [];
        using var reader = Reader((_, _, _) => Refuses("request.invalid"), tried);

        var error = await Assert.ThrowsAsync<ScreenReadException>(() => ReadAsync(reader));
        Assert.False(error.Busy);
        Assert.Equal("gpu-pc's Reading role: request.invalid", error.Message);
        Assert.Equal(["gpu-pc"], tried);
    }

    [Fact]
    public void Mcp_reading_status_gives_the_list_and_the_places_the_desktop_tries()
    {
        Save(ReadingPool.Windows(), PoolMember.Computer("gpu-pc").WithSetting(PoolSettingKeys.Model, "ppocrv5-server"), PoolMember.Computer("nas-host"));
        var status = System.Text.Json.JsonSerializer.SerializeToElement(Martlet.Mcp.ReadingPoolCheck.Status(directory, "desk-1"));

        Assert.Equal("saved", status.GetProperty("list").GetString());
        Assert.False(status.GetProperty("off").GetBoolean());
        Assert.Equal("ppocrv5-server", status.GetProperty("members")[1].GetProperty("model").GetString());
        Assert.False(status.GetProperty("members")[2].GetProperty("paired").GetBoolean());
        Assert.Equal(ReadingList.Targets(directory).Select(t => t.Target.Key),
            status.GetProperty("tries").EnumerateArray().Select(e => e.GetProperty("key").GetString()));
    }
}
