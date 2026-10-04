using System.Security.Cryptography;
using Martlet.Core.Nodes;

namespace Martlet.Gateway.Tests;

/// <summary>The command mailbox hands an agent that runs commands side by side every command it may start now
/// (<see cref="NodeCommandSchedule"/>), and an older agent one at a time.</summary>
public sealed class GatewayCommandScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private readonly string token = Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
    private readonly GatewayCommandStore store = new();
    private readonly GatewayPrincipal agent = Principal("host-pc");
    private readonly GatewayPrincipal desk = Principal("desk");
    private int sent;

    public GatewayCommandScheduleTests() => store.Attach(new Storage(), token, Now);

    private static GatewayPrincipal Principal(string device) => new()
    {
        HostId = "check-host", CredentialId = "credential-" + device, DeviceId = device, Role = GatewayRole.Voice,
        CredentialLifetime = new PairedDeviceLifetime()
    };

    private NodeCommand Send(string kind, Dictionary<string, string> arguments) =>
        store.Submit(desk, kind, arguments, new Dictionary<string, string>(), Now.AddSeconds(++sent),
            Base64Url.Encode(RandomNumberGenerator.GetBytes(16)));

    private NodeCommand Add(string role) => Send(NodeCommandKinds.AddRole, new() { ["role"] = role });

    private NodeCommand? Take(params NodeCommand[] running) =>
        store.Poll(agent, token, "0.39.0", NodeCommandKinds.All, running.Select(c => c.Id).ToArray(), Now.AddSeconds(++sent)).Command;

    private NodeCommand? TakeOneAtATime() => store.Poll(agent, token, "0.38.1", NodeCommandKinds.All, null, Now.AddSeconds(++sent)).Command;

    private void Finish(NodeCommand command) =>
        store.Report(agent, token, command.Id, NodeCommandState.Succeeded, "done", [], 0, Now.AddSeconds(++sent));

    [Fact]
    public void Installs_of_different_roles_start_side_by_side()
    {
        var thinking = Add("deep-thinking");
        var singing = Add("singing");
        var face = Add("audio2face");

        var first = Take();
        var second = Take(first!);
        var third = Take(first!, second!);

        Assert.Equal([thinking.Id, singing.Id, face.Id], [first!.Id, second!.Id, third!.Id]);
        Assert.Null(Take(first, second, third));
        Assert.True(store.List(Now).Agent!.Parallel);
    }

    [Fact]
    public void A_second_change_to_one_role_waits_while_others_pass_it()
    {
        var singing = Add("singing");
        var running = Take()!;
        var again = Send(NodeCommandKinds.RemoveRole, new() { ["role"] = "singing" });
        var other = Add("ollama");

        Assert.Equal(singing.Id, running.Id);
        Assert.Equal(other.Id, Take(running)!.Id);
        Finish(running);
        Assert.Equal(again.Id, Take(store.Get(other.Id, Now))!.Id);
    }

    [Fact]
    public void An_update_waits_for_running_commands_and_holds_later_ones()
    {
        var install = Take(Add("singing"))  ?? Take()!;
        var update = Send(NodeCommandKinds.Update, new() { ["version"] = "0.39.0" });
        var later = Add("f5");

        Assert.Null(Take(install));
        Finish(install);
        var taken = Take()!;
        Assert.Equal(update.Id, taken.Id);
        Assert.Null(Take(taken));
        Finish(taken);
        Assert.Equal(later.Id, Take()!.Id);
    }

    [Fact]
    public void A_command_the_agent_does_not_list_as_running_is_handed_back_to_resume()
    {
        var install = Add("singing");
        var taken = Take()!;
        Add("ollama");

        var (command, _, resumed) = store.Poll(agent, token, "0.39.0", NodeCommandKinds.All, [], Now.AddSeconds(++sent));

        Assert.Equal(install.Id, taken.Id);
        Assert.Equal(install.Id, command!.Id);
        Assert.True(resumed);
    }

    [Fact]
    public void An_older_agent_gets_one_command_at_a_time()
    {
        var first = Add("singing");
        var second = Add("ollama");

        var taken = TakeOneAtATime()!;
        var again = store.Poll(agent, token, "0.38.1", NodeCommandKinds.All, null, Now.AddSeconds(++sent));

        Assert.Equal(first.Id, taken.Id);
        Assert.Equal(first.Id, again.Command!.Id);
        Assert.True(again.Resumed);
        Assert.False(store.List(Now).Agent!.Parallel);
        Finish(taken);
        Assert.Equal(second.Id, TakeOneAtATime()!.Id);
    }

    [Fact]
    public void A_running_list_must_be_command_ids_within_the_bound()
    {
        Assert.Equal("request.invalid", Assert.Throws<GatewayProtocolException>(() =>
            store.Poll(agent, token, "0.39.0", NodeCommandKinds.All, ["not an id"], Now)).Failure.Code);
        var tooMany = Enumerable.Range(0, GatewayCommandStore.MaximumActive + 1)
            .Select(_ => Base64Url.Encode(RandomNumberGenerator.GetBytes(16))).ToArray();
        Assert.Equal("request.invalid", Assert.Throws<GatewayProtocolException>(() =>
            store.Poll(agent, token, "0.39.0", NodeCommandKinds.All, tooMany, Now)).Failure.Code);
    }

    private sealed class Storage : IGatewayCommandStorage
    {
        private byte[]? saved;
        public byte[]? Load() => saved;
        public void Save(byte[] bytes) => saved = bytes;
    }
}
