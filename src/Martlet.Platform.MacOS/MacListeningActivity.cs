using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Martlet.Platform.MacOS.Native;

namespace Martlet.Platform.MacOS;

/// <summary>Keeps Martlet responsive while it listens, speaks or watches: an NSProcessInfo activity (no App Nap, no timer
/// throttling while hidden behind a game) plus an IOKit "prevent idle system sleep" assertion. The display may still sleep.
/// Dispose when listening stops.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacListeningActivity : IDisposable
{
    // NSActivityUserInitiated (0x00FFFFFF) | NSActivityIdleSystemSleepDisabled (1 << 20); the latter is already part of
    // the former but is spelled out because it is the point.
    internal const ulong ActivityOptions = 0x00FFFFFFUL | 1UL << 20;
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const uint AssertionLevelOn = 255;

    private nint activity;
    private uint assertion;

    [DllImport(IOKit)] private static extern int IOPMAssertionCreateWithName(nint type, uint level, nint name, out uint id);
    [DllImport(IOKit)] private static extern int IOPMAssertionRelease(uint id);

    public MacListeningActivity(string reason = "Martlet is listening")
    {
        var text = ObjC.NewString(reason);
        try
        {
            var info = ObjC.Send(ObjC.Class("NSProcessInfo"), "processInfo");
            activity = ObjC.Retain(ObjC.SendULongPtr(info, "beginActivityWithOptions:reason:", ActivityOptions, text));
        }
        finally { ObjC.Release(text); }

        var type = CF.String("PreventUserIdleSystemSleep");
        var name = CF.String(reason);
        try
        {
            if (IOPMAssertionCreateWithName(type, AssertionLevelOn, name, out var id) == 0) assertion = id;
        }
        finally
        {
            CF.CFRelease(type);
            CF.CFRelease(name);
        }
    }

    public void Dispose()
    {
        if (activity != 0)
        {
            ObjC.SendVoid(ObjC.Send(ObjC.Class("NSProcessInfo"), "processInfo"), "endActivity:", activity);
            ObjC.Release(activity);
            activity = 0;
        }
        if (assertion != 0)
        {
            IOPMAssertionRelease(assertion);
            assertion = 0;
        }
    }
}
