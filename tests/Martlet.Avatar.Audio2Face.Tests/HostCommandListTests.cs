using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Nodes;

namespace Martlet.Avatar.Audio2Face.Tests;

public sealed class HostCommandListTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private int sent;

    private NodeCommand Command(string kind, NodeCommandState state, string? role = null) => new()
    {
        Id = $"command-{++sent:D14}", Kind = kind, RequestedBy = "desktop-miku", RequestedAt = Start.AddSeconds(sent), UpdatedAt = Start,
        State = state, Arguments = kind == NodeCommandKinds.Update ? new Dictionary<string, string> { ["version"] = "0.39.0" }
            : role is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["role"] = role }
    };

    private static HostCommandList List(bool parallel, params NodeCommand[] oldestFirst) =>
        new(new NodeAgentInfo { DeviceId = "imouto-host", SeenAt = Start, Parallel = parallel }, oldestFirst.Reverse().ToArray());

    [Fact]
    public void A_parallel_host_starts_another_role_while_one_installs()
    {
        var thinking = Command(NodeCommandKinds.AddRole, NodeCommandState.Running, "deep-thinking");
        var singing = Command(NodeCommandKinds.AddRole, NodeCommandState.Queued, "singing");

        var list = List(parallel: true, thinking, singing);

        Assert.Null(list.Ahead(singing));
        Assert.Null(list.WaitingText(singing, "imouto-host"));
    }

    [Fact]
    public void A_host_that_runs_one_at_a_time_says_what_it_is_busy_with()
    {
        var thinking = Command(NodeCommandKinds.AddRole, NodeCommandState.Running, "deep-thinking");
        var singing = Command(NodeCommandKinds.AddRole, NodeCommandState.Queued, "singing");

        var text = List(parallel: false, thinking, singing).WaitingText(singing, "imouto-host");

        Assert.Equal("Martlet on imouto-host is busy with: Install deep-thinking (from desktop-miku). This runs next.", text);
    }

    [Fact]
    public void The_same_role_and_an_update_say_why_they_wait()
    {
        var singing = Command(NodeCommandKinds.AddRole, NodeCommandState.Running, "singing");
        var again = Command(NodeCommandKinds.RemoveRole, NodeCommandState.Queued, "singing");
        var update = Command(NodeCommandKinds.Update, NodeCommandState.Queued);
        var later = Command(NodeCommandKinds.Status, NodeCommandState.Queued);
        var list = List(parallel: true, singing, again, update, later);

        Assert.StartsWith("Martlet on imouto-host is already changing singing: Install singing", list.WaitingText(again, "imouto-host"));
        Assert.StartsWith("Martlet on imouto-host updates as soon as what it runs now ends: Install singing", list.WaitingText(update, "imouto-host"));
        Assert.Equal("Waiting behind Update to Martlet 0.39.0 (from desktop-miku), sent earlier: Martlet on imouto-host updates first.",
            list.WaitingText(later, "imouto-host"));
    }
}
