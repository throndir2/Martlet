using Martlet.Core.Nodes;

namespace Martlet.Core.Tests;

public sealed class NodeCommandScheduleTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static int next;

    private static NodeCommand Command(string kind, NodeCommandState state, string? role = null, string? version = null)
    {
        var at = Start.AddSeconds(Interlocked.Increment(ref next));
        var arguments = new Dictionary<string, string>();
        if (role is not null) arguments["role"] = role;
        if (version is not null) arguments["version"] = version;
        return new()
        {
            Id = Guid.NewGuid().ToString("N")[..22], Kind = kind, Arguments = arguments, RequestedBy = "desk", RequestedAt = at,
            State = state, UpdatedAt = at
        };
    }

    private static NodeCommand Add(string role, NodeCommandState state = NodeCommandState.Queued) => Command(NodeCommandKinds.AddRole, state, role);

    [Fact]
    public void Other_roles_status_and_reading_a_role_start_beside_running_changes()
    {
        var installing = Add("deep-thinking", NodeCommandState.Running);
        var singing = Add("singing");
        var status = Command(NodeCommandKinds.Status, NodeCommandState.Queued);
        var reading = Command(NodeCommandKinds.DescribeRole, NodeCommandState.Queued, "deep-thinking");
        NodeCommand[] commands = [installing, singing, status, reading];

        Assert.Null(NodeCommandSchedule.Blocker(singing, commands, parallel: true));
        Assert.Null(NodeCommandSchedule.Blocker(status, commands, parallel: true));
        Assert.Null(NodeCommandSchedule.Blocker(reading, commands, parallel: true));
    }

    [Fact]
    public void A_second_change_to_one_role_waits_for_the_first_running_or_waiting()
    {
        var installing = Add("singing", NodeCommandState.Running);
        var removing = Command(NodeCommandKinds.RemoveRole, NodeCommandState.Queued, "singing");
        var again = Add("singing");
        NodeCommand[] commands = [installing, removing, again];

        Assert.Same(installing, NodeCommandSchedule.Blocker(removing, commands, parallel: true));
        Assert.Same(installing, NodeCommandSchedule.Blocker(again, commands, parallel: true));
        Assert.Same(removing, NodeCommandSchedule.Blocker(again, [removing, again], parallel: true));
    }

    [Fact]
    public void An_update_waits_for_what_runs_and_holds_the_commands_sent_after_it()
    {
        var installing = Add("ollama", NodeCommandState.Running);
        var earlier = Add("singing");
        var update = Command(NodeCommandKinds.Update, NodeCommandState.Queued, version: "0.39.0");
        var later = Add("f5");
        NodeCommand[] commands = [installing, earlier, update, later];

        Assert.Same(installing, NodeCommandSchedule.Blocker(update, commands, parallel: true));
        Assert.Null(NodeCommandSchedule.Blocker(earlier, commands, parallel: true));
        Assert.Same(update, NodeCommandSchedule.Blocker(later, commands, parallel: true));
        Assert.Same(earlier, NodeCommandSchedule.Blocker(update, [earlier, update], parallel: true));
        Assert.Null(NodeCommandSchedule.Blocker(update, [update, later], parallel: true));
    }

    [Fact]
    public void A_running_update_holds_everything()
    {
        var update = Command(NodeCommandKinds.Update, NodeCommandState.Running, version: "0.39.0");
        var status = Command(NodeCommandKinds.Status, NodeCommandState.Queued);

        Assert.Same(update, NodeCommandSchedule.Blocker(status, [update, status], parallel: true));
    }

    [Fact]
    public void An_agent_that_runs_one_at_a_time_takes_the_oldest_after_the_running_one()
    {
        var reading = Command(NodeCommandKinds.DescribeRole, NodeCommandState.Running, "ollama");
        var first = Add("singing");
        var second = Command(NodeCommandKinds.Status, NodeCommandState.Queued);

        Assert.Same(reading, NodeCommandSchedule.Blocker(first, [reading, first, second], parallel: false));
        Assert.Same(first, NodeCommandSchedule.Blocker(second, [first, second], parallel: false));
        Assert.Null(NodeCommandSchedule.Blocker(first, [first, second], parallel: false));
    }

    [Fact]
    public void Commands_that_are_not_waiting_wait_for_nothing()
    {
        var running = Add("singing", NodeCommandState.Running);
        var done = Add("singing", NodeCommandState.Succeeded);

        Assert.Null(NodeCommandSchedule.Blocker(running, [running, done], parallel: true));
        Assert.Null(NodeCommandSchedule.Blocker(done, [running, done], parallel: false));
    }

    [Fact]
    public void Order_follows_the_list_when_times_tie()
    {
        var first = Add("singing");
        var second = Add("singing") with { RequestedAt = first.RequestedAt };

        Assert.Same(first, NodeCommandSchedule.Blocker(second, [first, second], parallel: true));
        Assert.Null(NodeCommandSchedule.Blocker(first, [first, second], parallel: true));
    }
}
