using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class LocalProcessTests
{
    [Fact]
    public void Clean_keeps_only_the_last_frame_of_a_cursor_redrawn_spinner()
    {
        // ollama pull without a terminal: each frame hides the cursor, moves to column 1 and clears the rest of the line.
        var frames = string.Concat("⠋⠙⠹".Select(c => $"\x1b[?25l\x1b[1Gpulling manifest {c} \x1b[K\x1b[?25h"));
        Assert.Equal("pulling manifest", LocalProcess.Clean(frames));
    }

    [Fact]
    public void Clean_treats_cursor_up_as_a_redraw_and_still_drops_progress_bars()
    {
        Assert.Equal("verifying sha256 digest", LocalProcess.Clean("\x1b[A\x1b[1Gpulling 4c27e0f5b5ad: 100% ▕████▏ 9.6 GB\x1b[K\x1b[A\x1b[1Gverifying sha256 digest\x1b[K"));
        Assert.Null(LocalProcess.Clean("\x1b[1Gpulling 4c27e0f5b5ad:  45% ▕██  ▏ 4.3 GB/9.6 GB\x1b[K"));
    }

    [Fact]
    public void Clean_keeps_plain_lines_and_carriage_return_redraws()
    {
        Assert.Equal("Error: pull model manifest: i/o timeout", LocalProcess.Clean("Error: pull model manifest: i/o timeout"));
        Assert.Equal("done", LocalProcess.Clean("10%\r50%\rdone\r"));
        Assert.Equal("Starting", LocalProcess.Clean("\x1b[1mStarting\x1b[0m"));
    }
}
