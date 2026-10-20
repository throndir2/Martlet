using System.Runtime.InteropServices;
using Martlet.Mcp;

namespace Martlet.Desktop.Tests;

/// <summary>DesktopAutomation reads again when UI Automation gives up on a call (UIA_E_TIMEOUT) because Martlet does not answer in
/// time on a busy PC (#1098), at most three times, and never for another error.</summary>
public sealed class DesktopAutomationReadingTests
{
    private static COMException Timeout() => new("Operation timed out.", DesktopAutomation.UiaTimeout);

    [Fact]
    public void ATimedOutReadIsTriedAgainAndThenAnswers()
    {
        var attempts = 0;
        var waits = new List<TimeSpan>();
        var value = DesktopAutomation.Reading(() => ++attempts < 3 ? throw Timeout() : "read", waits.Add);
        Assert.Equal("read", value);
        Assert.Equal(3, attempts);
        Assert.Equal([TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500)], waits);
    }

    [Fact]
    public void AReadThatKeepsTimingOutStopsAfterThreeTries()
    {
        var attempts = 0;
        var error = Assert.Throws<COMException>(() => DesktopAutomation.Reading<string>(() => { attempts++; throw Timeout(); }, _ => { }));
        Assert.Equal(DesktopAutomation.UiaTimeout, error.HResult);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public void OtherErrorsAreNotTriedAgain()
    {
        var attempts = 0;
        Assert.Throws<COMException>(() => DesktopAutomation.Reading<string>(() => { attempts++; throw new COMException("other", unchecked((int)0x80004005)); }, _ => { }));
        Assert.Equal(1, attempts);
        attempts = 0;
        Assert.Throws<InvalidOperationException>(() => DesktopAutomation.Reading<string>(() => { attempts++; throw new InvalidOperationException(); }, _ => { }));
        Assert.Equal(1, attempts);
    }
}
