using System.Net.Http;
using System.Net.Sockets;
using Martlet.Avatar.Audio2Face.Remote;

namespace Martlet.Avatar.Audio2Face.Tests;

/// <summary>Host status for the desktop log (once per change, one missed check is not a change) and the words for a PC that
/// ran out of network resources.</summary>
public sealed class HostAnswersTests
{
    [Fact]
    public void A_busy_host_that_misses_single_checks_is_never_reported_as_stopped()
    {
        // The pattern in a real desktop log: every second or third 15-second check of a busy host timed out and the next answered.
        var answers = new HostAnswers(2);
        Assert.Null(answers.Record("diva-host", true, null));
        var outcomes = new[] { false, true, false, true, true, false, true, false, true };
        foreach (var answered in outcomes)
        {
            Assert.Null(answers.Record("diva-host", answered, answered ? null : "Didn't respond in time."));
            Assert.True(answers.Answering("diva-host"));
        }
    }

    [Fact]
    public void A_host_that_stops_answering_is_reported_once_and_once_again_when_it_is_back()
    {
        var answers = new HostAnswers(2);
        answers.Record("diva-host", true, null);

        Assert.Null(answers.Record("diva-host", false, "Didn't respond in time."));
        Assert.True(answers.Answering("diva-host"));
        Assert.Equal(1, answers.Misses("diva-host"));

        var down = answers.Record("diva-host", false, "Could not reach the Martlet host over pinned TLS.");
        Assert.NotNull(down);
        Assert.False(down.Answering);
        Assert.Equal("Host diva-host stopped answering: Could not reach the Martlet host over pinned TLS.", down.Text);
        Assert.False(answers.Answering("diva-host"));

        for (var i = 0; i < 10; i++) Assert.Null(answers.Record("diva-host", false, "Didn't respond in time."));

        var back = answers.Record("diva-host", true, null);
        Assert.NotNull(back);
        Assert.True(back.Answering);
        Assert.Equal("Host diva-host answers again.", back.Text);
        Assert.Null(answers.Record("diva-host", true, null));
        Assert.Equal(0, answers.Misses("diva-host"));
    }

    [Fact]
    public void A_host_that_never_answered_since_start_says_it_didnt_answer_and_forgetting_starts_over()
    {
        var answers = new HostAnswers(2);
        Assert.Null(answers.Record("miku-host", false, "Didn't respond in time."));
        Assert.Equal("Host miku-host didn't answer: Didn't respond in time.", answers.Record("miku-host", false, "Didn't respond in time.")?.Text);
        Assert.Equal("Host miku-host answers again.", answers.Record("miku-host", true, null)?.Text);

        // Other hosts are counted on their own.
        Assert.Null(answers.Record("imouto-host", false, null));
        Assert.True(answers.Answering("imouto-host"));
        Assert.Equal("Host imouto-host didn't answer", answers.Record("imouto-host", false, " ")?.Text);

        answers.Forget("imouto-host");
        Assert.True(answers.Answering("imouto-host"));
        Assert.Equal(0, answers.Misses("imouto-host"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HostAnswers(0));
    }

    [Fact]
    public void A_pc_out_of_network_resources_is_named_as_such_not_blamed_on_the_host()
    {
        var shortage = new SocketException((int)SocketError.NoBufferSpaceAvailable);
        Assert.Equal(SocketError.NoBufferSpaceAvailable, HostRoutes.Shortage(shortage));
        // The handler wraps what the connection callback threw.
        Assert.Equal(SocketError.NoBufferSpaceAvailable,
            HostRoutes.Shortage(new HttpRequestException("The connection failed.", new IOException("inner", shortage))));
        Assert.Equal(SocketError.TooManyOpenSockets, HostRoutes.Shortage(new SocketException((int)SocketError.TooManyOpenSockets)));
        Assert.Null(HostRoutes.Shortage(new SocketException((int)SocketError.ConnectionRefused)));
        Assert.Null(HostRoutes.Shortage(new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused))));
        Assert.Null(HostRoutes.Shortage(null));

        Assert.Equal("this PC is out of network resources", HostRoutes.Describe(shortage));
        Assert.Equal("refused", HostRoutes.Describe(new SocketException((int)SocketError.ConnectionRefused)));
        Assert.Equal("no answer in time", HostRoutes.Describe(new OperationCanceledException()));

        var text = HostRoutes.ShortageText("192.168.50.45:9443", SocketError.NoBufferSpaceAvailable);
        Assert.StartsWith("This PC ran out of network resources, so it couldn't open a connection to 192.168.50.45:9443", text, StringComparison.Ordinal);
        Assert.Contains("NoBufferSpaceAvailable", text, StringComparison.Ordinal);
        Assert.Contains("The host may be fine.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("didn't answer", text, StringComparison.Ordinal);
        Assert.DoesNotContain("outside addresses", text, StringComparison.Ordinal);
    }
}
