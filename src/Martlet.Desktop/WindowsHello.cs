using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Martlet.Desktop;

internal enum WindowsHelloAvailability { Available, DeviceNotPresent, NotConfiguredForUser, DisabledByPolicy, DeviceBusy, Unknown }

internal enum WindowsHelloResult { Verified, DeviceNotPresent, NotConfiguredForUser, DisabledByPolicy, DeviceBusy, RetriesExhausted, Canceled, Failed }

/// <summary>
/// Windows Hello as a way to unlock an account on this PC (docs/ACCOUNTS.md, Unlock): Windows' own check that the person at the
/// PC is this Windows login's user (face, fingerprint or the Windows PIN), through <c>Windows.Security.Credentials.UI.
/// UserConsentVerifier</c> called without Windows Runtime projections (as the Windows OCR reader of Discord calls does),
/// so Martlet.Desktop stays <c>net10.0-windows</c>. Windows Hello can't tell apart people who share one Windows login (any
/// finger or face enrolled there, and the Windows PIN, pass), so it keeps accounts apart only when nobody else enrolled on that
/// Windows login. It releases no key, so it can't open encrypted files.
/// </summary>
internal static unsafe class WindowsHello
{
    private const string ClassName = "Windows.Security.Credentials.UI.UserConsentVerifier";
    private static readonly Guid Statics = new("AF4F3F91-564C-4DDC-B8B5-973447627C65");
    private static readonly Guid Interop = new("39E050C3-4E74-441A-8DC0-B81104DF949C");
    private static readonly Guid AsyncInfo = new("00000036-0000-0000-C000-000000000046");
    private static readonly Guid AsyncOperation = new("9fc2b0bb-e446-44e2-aa61-9cab8f636af2");
    private static readonly Guid TypeNamespace = new("11f47ad5-7b73-42c0-abae-878b1e16adee");

    /// <summary>The interface ID of IAsyncOperation&lt;UserConsentVerificationResult&gt; by the Windows Runtime's rule for
    /// parameterized interfaces (a version 5 UUID of the type signature).</summary>
    internal static Guid VerificationOperation { get; } =
        Parameterized(AsyncOperation, "enum(Windows.Security.Credentials.UI.UserConsentVerificationResult;i4)");

    /// <summary>The interface ID of IAsyncOperation&lt;UserConsentVerifierAvailability&gt;.</summary>
    internal static Guid AvailabilityOperation { get; } =
        Parameterized(AsyncOperation, "enum(Windows.Security.Credentials.UI.UserConsentVerifierAvailability;i4)");

    /// <summary>Whether Windows Hello is set up for this Windows login; <see cref="WindowsHelloAvailability.Unknown"/> when
    /// Windows can't say (an older Windows, a service session).</summary>
    internal static Task<WindowsHelloAvailability> AvailabilityAsync(CancellationToken cancellationToken = default) =>
        Task.Factory.StartNew(() =>
        {
            try
            {
                Apartment();
                nint statics = 0, operation = 0;
                try
                {
                    statics = Factory(Statics);
                    Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(statics, 6))(statics, &operation));
                    return Wait(operation, TimeSpan.FromSeconds(10), cancellationToken) is { } value && value is >= 0 and <= 4
                        ? (WindowsHelloAvailability)value : WindowsHelloAvailability.Unknown;
                }
                finally
                {
                    Release(operation);
                    Release(statics);
                }
            }
            catch (Exception error) when (error is COMException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
            {
                return WindowsHelloAvailability.Unknown;
            }
        }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>Shows Windows Hello over <paramref name="window"/> with <paramref name="message"/> and waits for the person
    /// (at most two minutes).</summary>
    internal static Task<WindowsHelloResult> VerifyAsync(nint window, string message, CancellationToken cancellationToken = default) =>
        Task.Factory.StartNew(() =>
        {
            try
            {
                Apartment();
                nint interop = 0, text = 0, operation = 0;
                try
                {
                    interop = Factory(Interop);
                    Check(WindowsCreateString(message, (uint)message.Length, &text));
                    var iid = VerificationOperation;
                    Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, Guid*, nint*, int>)Slot(interop, 6))(interop, window, text, &iid, &operation));
                    return Wait(operation, TimeSpan.FromMinutes(2), cancellationToken) is { } value && value is >= 0 and <= 6
                        ? (WindowsHelloResult)value : WindowsHelloResult.Canceled;
                }
                finally
                {
                    if (text != 0) WindowsDeleteString(text);
                    Release(operation);
                    Release(interop);
                }
            }
            catch (Exception error) when (error is COMException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
            {
                ErrorLog.Warn($"Windows Hello could not run ({error.GetType().Name}: {error.Message}).");
                return WindowsHelloResult.Failed;
            }
        }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    internal static string Describe(WindowsHelloAvailability availability) => availability switch
    {
        WindowsHelloAvailability.Available => "Windows Hello is set up on this Windows login.",
        WindowsHelloAvailability.DeviceNotPresent => "This PC has no Windows Hello camera or fingerprint reader.",
        WindowsHelloAvailability.NotConfiguredForUser => "Windows Hello isn't set up for this Windows login (Windows Settings › Accounts › Sign-in options).",
        WindowsHelloAvailability.DisabledByPolicy => "Windows Hello is turned off by a policy on this PC.",
        WindowsHelloAvailability.DeviceBusy => "Windows Hello is busy; try again.",
        _ => "Windows can't say whether Windows Hello is set up here."
    };

    internal static string Describe(WindowsHelloResult result) => result switch
    {
        WindowsHelloResult.Verified => "Windows Hello recognized you.",
        WindowsHelloResult.RetriesExhausted => "Windows Hello didn't recognize you too many times.",
        WindowsHelloResult.Canceled => "Windows Hello was canceled.",
        WindowsHelloResult.Failed => "Windows Hello could not run.",
        _ => Describe((WindowsHelloAvailability)(int)result)
    };

    // Polls the operation's IAsyncInfo, then reads its enum result (IAsyncOperation<T>.GetResults, slot 8).
    private static int? Wait(nint operation, TimeSpan limit, CancellationToken cancellationToken)
    {
        nint info = 0;
        try
        {
            info = Query(operation, AsyncInfo);
            var started = Environment.TickCount64;
            int status;
            while (true)
            {
                Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(info, 7))(info, &status));
                if (status != 0) break;
                if (cancellationToken.IsCancellationRequested || Environment.TickCount64 - started > limit.TotalMilliseconds)
                {
                    ((delegate* unmanaged[Stdcall]<nint, int>)Slot(info, 9))(info);
                    return null;
                }
                Thread.Sleep(20);
            }
            if (status != 1) return null;
            int value;
            Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(operation, 8))(operation, &value));
            return value;
        }
        finally
        {
            if (info != 0) ((delegate* unmanaged[Stdcall]<nint, int>)Slot(info, 10))(info);
            Release(info);
        }
    }

    internal static Guid Parameterized(Guid generic, string argument)
    {
        var signature = Encoding.UTF8.GetBytes($"pinterface({{{generic.ToString("D").ToLowerInvariant()}}};{argument})");
        var input = new byte[16 + signature.Length];
        TypeNamespace.TryWriteBytes(input, bigEndian: true, out _);
        signature.CopyTo(input, 16);
        var hash = SHA1.HashData(input);
        hash[6] = (byte)(hash[6] & 0x0F | 0x50);
        hash[8] = (byte)(hash[8] & 0x3F | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    /// <summary>Whether <paramref name="iid"/> is an interface the operation of <c>CheckAvailabilityAsync</c> has (a check of
    /// <see cref="Parameterized"/> without showing anything).</summary>
    internal static bool AvailabilityOperationHas(Guid iid)
    {
        Apartment();
        nint statics = 0, operation = 0, found = 0;
        try
        {
            statics = Factory(Statics);
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(statics, 6))(statics, &operation));
            var copy = iid;
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(operation, 0))(operation, &copy, &found) >= 0 && found != 0;
        }
        finally
        {
            Release(found);
            Release(operation);
            Release(statics);
        }
    }

    private static nint Factory(Guid iid)
    {
        nint text = 0, factory = 0;
        Check(WindowsCreateString(ClassName, (uint)ClassName.Length, &text));
        try { Check(RoGetActivationFactory(text, &iid, &factory)); }
        finally { WindowsDeleteString(text); }
        return factory;
    }

    private static nint Query(nint instance, Guid iid)
    {
        nint result = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(instance, 0))(instance, &iid, &result));
        return result;
    }

    private static void* Slot(nint instance, int slot)
    {
        if (instance == 0) throw new InvalidOperationException("A Windows Runtime object is missing.");
        return (*(void***)instance)[slot];
    }

    private static void Release(nint instance)
    {
        if (instance != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(instance, 2))(instance);
    }

    private static void Check(int result)
    {
        if (result < 0) throw new COMException("Windows Hello failed.", result);
    }

    // The thread joins the multithreaded apartment (already joined, or another apartment, is fine).
    private static void Apartment() => CoInitializeEx(0, 0);

    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint classId, Guid* iid, nint* factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string source, uint length, nint* text);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint text);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, int model);
}
