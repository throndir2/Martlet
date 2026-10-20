using Martlet.Core.Settings;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

/// <summary>What the job pages decide without WPF (<see cref="JobPageModel"/>): the place in use before a choice, the Now line
/// and a computer's line in Your computers.</summary>
public sealed class JobPageModelTests
{
    [Fact]
    public void Before_a_choice_the_page_shows_this_pc_or_the_networks_computer()
    {
        Assert.Equal(MainWindow.JobPlace.ThisPc, JobPageModel.PlaceInUse(SetupRole.Llm, null, null, null));
        Assert.Equal(MainWindow.JobPlace.ThisPc, JobPageModel.PlaceInUse(SetupRole.Llm, null, "pc-host", "pc-host"));
        Assert.Equal(MainWindow.JobPlace.Computer, JobPageModel.PlaceInUse(SetupRole.Llm, null, "gpu-box", "pc-host"));
    }

    [Fact]
    public void The_now_line_says_what_to_do_before_a_choice()
    {
        Assert.Equal("Not chosen yet: pick a voice engine below.", JobPageModel.NowStatus(CompanionTab.Voice, null, null, networkChose: false));
        Assert.Equal("Not chosen yet: this PC is recommended below.", JobPageModel.NowStatus(CompanionTab.Listening, null, null, networkChose: false));
        Assert.Equal("Not set up on this PC yet.", JobPageModel.NowStatus(CompanionTab.Thinking, null, null, networkChose: true));
    }

    [Fact]
    public void A_computers_line_says_what_it_runs_or_why_it_cant()
    {
        Assert.Equal("In use: Ollama.", JobPageModel.ComputerDetail(true, "In use: Ollama.", null, null, "Ollama", true));
        Assert.Equal("Unavailable: it is busy.", JobPageModel.ComputerDetail(false, "", "it is busy.", null, "Ollama", true));
        Assert.Equal("Ollama isn't installed there yet, but Martlet can set it up.", JobPageModel.ComputerDetail(false, "", null, null, "Ollama", true));
        Assert.Equal("Not reachable right now.", JobPageModel.ComputerDetail(false, "", null, null, "Ollama", false));
        Assert.Equal("Not checked yet.", JobPageModel.ComputerDetail(false, "", null, null, "Ollama", null));
        Assert.Equal("A friend shares it with this PC, and it offers Ollama.", JobPageModel.SharedDetail(false, "", true, "Ollama", true));
    }
}
