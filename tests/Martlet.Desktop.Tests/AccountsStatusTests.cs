using System.Text.Json;
using Martlet.Core.Accounts;
using Martlet.Core.Network;
using Martlet.Mcp;

namespace Martlet.Desktop.Tests;

public sealed class AccountsStatusTests
{
    private const string Sid = "S-1-5-21-1004336348-1177238915-682003330-1001";
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AccountsStatusReadsTheSessionAndTheDirectoryWithoutSidsOrEmail()
    {
        var data = Path.Combine(Path.GetTempPath(), "Martlet.Mcp.Accounts." + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(data);
            var none = await StatusAsync(data);
            Assert.Equal("none", none.GetProperty("session").GetString());
            Assert.Equal("none", none.GetProperty("directory").GetString());

            var device = DeviceIds.Ensure(data).Id;
            var session = AccountSession.Open(data, Sid, () => "Sam Doe", device, Now);
            var alex = session.AddPerson("Alex", Now);
            using var key = NetworkKey.Create(device);
            var roster = NetworkRoster.Found(key, "DESK", Now);
            var (directory, written) = AccountSession.Reconcile(AccountDirectory.Empty, session.State, roster, key, device, Now);
            session.Follow(directory.Put(key, directory.Find(alex)!.WithEmailHint(roster.NetworkId, "alex@example.com"), Now), [session.AccountId]);

            var status = await StatusAsync(data);
            var text = status.GetRawText();
            Assert.Equal(device, status.GetProperty("device").GetString());
            Assert.Equal("loaded", status.GetProperty("session").GetString());
            Assert.Equal("windows", status.GetProperty("windowsLogin").GetString());
            Assert.Equal("Sam Doe", status.GetProperty("current").GetProperty("name").GetString());
            Assert.Equal("owner", status.GetProperty("current").GetProperty("role").GetString());
            Assert.True(status.GetProperty("current").GetProperty("folder").GetBoolean());
            Assert.Equal(2, status.GetProperty("signedIn").GetArrayLength());
            Assert.Equal(1, status.GetProperty("pending").GetInt32());
            Assert.Equal(session.AccountId.ToString("N"), status.GetProperty("owner").GetString());
            Assert.Equal("loaded", status.GetProperty("directory").GetString());
            Assert.Equal(2, status.GetProperty("accounts").GetInt32());
            Assert.Equal(1, status.GetProperty("owners").GetInt32());
            var entry = status.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("id").GetString() == alex.ToString("N"));
            Assert.Equal("windows", entry.GetProperty("logins")[0].GetString());
            Assert.True(entry.GetProperty("thisDevice").GetBoolean());
            Assert.DoesNotContain(Sid, text);
            Assert.DoesNotContain("example.com", text);
            Assert.DoesNotContain(Account.EmailHintFor(roster.NetworkId, "alex@example.com"), text);
        }
        finally
        {
            if (Directory.Exists(data)) Directory.Delete(data, recursive: true);
        }
    }

    private static async Task<JsonElement> StatusAsync(string data)
    {
        var request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "accounts_status", arguments = new { dataDirectory = data } }
        });
        using var reader = new StringReader(request + "\n");
        using var writer = new StringWriter();
        await new McpServer(new DesktopAutomation(false)).RunAsync(reader, writer, CancellationToken.None);
        using var message = JsonDocument.Parse(writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]);
        var text = message.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        using var result = JsonDocument.Parse(text);
        return result.RootElement.Clone();
    }
}
