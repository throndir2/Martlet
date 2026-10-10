using System.Text.Json;
using System.Text.RegularExpressions;
using Martlet.Core.Network;

namespace Martlet.Core.Tests;

public sealed class DeviceIdsTests : IDisposable
{
    private static readonly Regex NewForm = new(@"\Adesktop-[a-z0-9._-]+-[a-z0-9]{6}\z");
    private readonly string root = Path.Combine(Path.GetTempPath(), "martlet-device-ids-" + Guid.NewGuid().ToString("N"));

    private string Folder(string name)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void A_new_data_folder_gets_a_new_id_that_is_saved_and_never_changes()
    {
        var folder = Folder("new");
        Assert.Null(DeviceIds.Peek(folder, "DIVA"));
        Assert.False(File.Exists(Path.Combine(folder, DeviceIds.FileName)));

        var first = DeviceIds.Ensure(folder, "DIVA");
        Assert.Equal(DeviceIdSource.New, first.Source);
        Assert.Matches(NewForm, first.Id);
        Assert.StartsWith("desktop-diva-", first.Id);
        Assert.True(DeviceIds.IsValid(first.Id));

        var again = DeviceIds.Ensure(folder, "OTHER-NAME");
        Assert.Equal(new DeviceIdChoice(first.Id, DeviceIdSource.Saved), again);
        Assert.Equal(again, DeviceIds.Peek(folder));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
    }

    [Fact]
    public void Two_windows_users_on_one_pc_get_different_ids()
    {
        var alex = DeviceIds.Ensure(Folder("alex"), "DIVA").Id;
        var sam = DeviceIds.Ensure(Folder("sam"), "DIVA").Id;
        Assert.NotEqual(alex, sam);
        Assert.StartsWith("desktop-diva-", alex);
        Assert.StartsWith("desktop-diva-", sam);
    }

    [Fact]
    public void A_paired_data_folder_keeps_the_id_its_pairings_use()
    {
        var folder = Folder("paired");
        File.WriteAllText(Path.Combine(folder, "hosts.json"), JsonSerializer.Serialize(new
        {
            version = 1,
            hosts = new object[]
            {
                new { pairing = new { hostId = "a-host", deviceId = "desktop-diva" } },
                new { pairing = new { hostId = "b-host", deviceId = "desktop-diva" } },
                new { pairing = new { hostId = "c-host", deviceId = "custom-id" } }
            }
        }));
        Assert.Equal(new DeviceIdChoice("desktop-diva", DeviceIdSource.Pairings), DeviceIds.Peek(folder, "DIVA"));
        Assert.Equal(new DeviceIdChoice("desktop-diva", DeviceIdSource.Pairings), DeviceIds.Ensure(folder, "DIVA"));
        Assert.Equal("desktop-diva", DeviceIds.Saved(folder));
    }

    [Fact]
    public void An_older_lip_sync_pairing_counts_as_a_pairing()
    {
        var folder = Folder("avatar");
        File.WriteAllText(Path.Combine(folder, "avatar.json"),
            """{"version":1,"remote_host":{"origin":"https://192.168.1.5:9443/","host_id":"gpu-host","device_id":"desktop-old-pc"}}""");
        Assert.Equal(new DeviceIdChoice("desktop-old-pc", DeviceIdSource.Pairings), DeviceIds.Ensure(folder, "DIVA"));
    }

    [Theory]
    [InlineData("network.json")]
    [InlineData("hosts.json")]
    [InlineData("network/device_ecdsa")]
    public void A_folder_that_was_in_a_network_keeps_the_older_form(string evidence)
    {
        var folder = Folder("network-" + evidence.Replace('/', '-'));
        var path = Path.Combine(folder, evidence);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not readable as pairings");
        Assert.Equal(new DeviceIdChoice("desktop-diva", DeviceIdSource.Legacy), DeviceIds.Ensure(folder, "DIVA"));
    }

    [Fact]
    public void An_unreadable_device_file_is_replaced()
    {
        var folder = Folder("broken");
        File.WriteAllText(Path.Combine(folder, DeviceIds.FileName), "{ not json");
        Assert.Null(DeviceIds.Saved(folder));
        var choice = DeviceIds.Ensure(folder, "DIVA");
        Assert.Equal(DeviceIdSource.New, choice.Source);
        Assert.Equal(choice.Id, DeviceIds.Saved(folder));
    }

    [Theory]
    [InlineData("DIVA", "desktop-diva")]
    [InlineData("Ünï Cödé!", "desktop-ncd")]
    [InlineData("###", "desktop-pc")]
    public void The_older_form_is_desktop_and_the_cleaned_computer_name(string name, string expected) =>
        Assert.Equal(expected, DeviceIds.Legacy(name));

    [Fact]
    public void A_long_computer_name_is_shortened_to_fit_64_characters()
    {
        var name = new string('a', 80);
        var id = DeviceIds.New(name);
        Assert.Equal(DeviceIds.MaximumLength, id.Length);
        Assert.Matches(NewForm, id);
        Assert.Equal(DeviceIds.MaximumLength, DeviceIds.Legacy(name).Length);
    }
}
