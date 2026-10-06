using System.Diagnostics;
using System.Runtime.InteropServices;
using Martlet.Audio;
using Martlet.Audio.Windows;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;
using Martlet.Discord.Calls;

namespace Martlet.Desktop;

/// <summary>Martlet in your own Discord calls (companion mode): the owner talks in a DM, group DM or server call on their own
/// Discord account and Martlet takes part through this PC. Martlet never automates Discord (no clicks, no typing, no token, no
/// account control): it only hears the Discord app's sound (<see cref="Sources"/>), looks at the Discord window's speaking
/// indicators on this PC (<see cref="Attribution"/>), plays its voice into the output the owner picks (<see cref="Output"/>,
/// such as a virtual cable the owner installed and chose as Discord's microphone) and can show the character in a camera view
/// for OBS. Its settings are <see cref="DiscordCallPreferences"/> (discord-calls.json).</summary>
internal sealed class DiscordCallService
{
    private readonly Lock gate = new();
    private readonly string? directory;
    private DiscordCallPreferences preferences;
    private IReadOnlyList<CallOutput>? outputs;
    private int capturing;

    internal DiscordCallService(string? directory, Func<DiscordCallPreferences, CallWindowSample?>? look = null,
        ICallTextReader? reader = null)
    {
        this.directory = directory;
        preferences = DiscordCallPreferences.Load(directory);
        Attribution = new(() => Preferences, look ?? DiscordWindow.Look, reader ?? new WindowsCallTextReader());
    }

    internal DiscordCallPreferences Preferences { get { lock (gate) return preferences; } }
    internal DiscordCallAttribution Attribution { get; }

    /// <summary>Raised on the thread that saved, after the settings changed.</summary>
    internal event Action? Changed;

    internal bool Save(Func<DiscordCallPreferences, DiscordCallPreferences> update)
    {
        lock (gate)
        {
            var next = update(preferences).Normalized();
            if (!next.Save(directory)) return false;
            preferences = next;
        }
        Changed?.Invoke();
        return true;
    }

    // ---------- hearing the call ----------

    /// <summary>What the PC listener hears when it opens: while the call mode is on with Discord only, Discord's own sound (a
    /// process loopback of Discord and the processes it started), otherwise <paramref name="everything"/> (every app but
    /// Martlet, Hear what this PC plays' usual source).</summary>
    internal IPcAudioSourceFactory Sources(IPcAudioSourceFactory everything) => new CallSources(this, everything);

    /// <summary>What the PC listener opened last: Discord alone (<c>"discord"</c>), everything but Martlet (<c>"system"</c>) or
    /// nothing yet (null).</summary>
    internal string? Capturing => Volatile.Read(ref capturing) switch { 1 => "discord", 2 => "system", _ => null };

    /// <summary>The PC listener should open again: the call mode wants Discord alone and Discord runs now (or stopped), or it
    /// no longer wants it.</summary>
    internal bool CaptureOutdated(bool listening)
    {
        if (!listening || Capturing is not { } now) return false;
        var wanted = Preferences is { On: true, Capture: DiscordCallCapture.DiscordApp } && DiscordWindow.Process() is not null
            ? "discord" : "system";
        return now != wanted;
    }

    private sealed class CallSources(DiscordCallService calls, IPcAudioSourceFactory everything) : IPcAudioSourceFactory
    {
        public IPcAudioSource Open(CancellationToken cancellationToken)
        {
            if (calls.Preferences is { On: true, Capture: DiscordCallCapture.DiscordApp } && DiscordWindow.Process() is { } discord)
            {
                try
                {
                    var source = WasapiPcAudioSourceFactory.OpenApp(discord, "Discord", cancellationToken);
                    Volatile.Write(ref calls.capturing, 1);
                    return source;
                }
                catch (Exception error) when (error is CaptureDeviceException or PlatformNotSupportedException or COMException or
                    InvalidOperationException or AggregateException)
                {
                    ErrorLog.Warn($"Discord call: Martlet can't hear the Discord app alone ({error.GetType().Name}), so it hears " +
                        "everything this PC plays except itself.");
                }
            }
            var opened = everything.Open(cancellationToken);
            Volatile.Write(ref calls.capturing, 2);
            return opened;
        }
    }

    // ---------- speaking into the call ----------

    /// <summary>Martlet's voice output: while the call mode is on and an output is chosen, the voice goes there (and also to
    /// <paramref name="speakers"/>' usual output when asked); otherwise exactly as before.</summary>
    internal IPlaybackDeviceFactory Output(IPlaybackDeviceFactory speakers) => new CallOutputDevices(this, speakers);

    /// <summary>The outputs Windows listed at the last refresh (null until listed).</summary>
    internal IReadOnlyList<CallOutput>? Outputs { get => Volatile.Read(ref outputs); set => Volatile.Write(ref outputs, value); }

    internal CallOutputPlan Plan => DiscordCallOutputs.Plan(Preferences, Outputs);

    /// <summary>Lists the playback devices again (names only; nothing is opened).</summary>
    internal async Task RefreshOutputsAsync(CancellationToken token)
    {
        var list = await new AudioDevicePresence().CheckAsync(token).ConfigureAwait(false);
        if (list is not null) Outputs = list.Outputs.Select(output => new CallOutput(output.EndpointId, output.DisplayName, output.IsDefault)).ToArray();
    }

    private sealed class CallOutputDevices(DiscordCallService calls, IPlaybackDeviceFactory speakers) : IPlaybackDeviceFactory
    {
        public IPlaybackDevice Open(OutputSelection selection, PcmFormat format, CancellationToken cancellationToken)
        {
            if (!calls.Preferences.On || calls.Plan is not { OutputId: { } id } plan) return speakers.Open(selection, format, cancellationToken);
            var call = speakers.Open(new OutputSelection(OutputPolicy.FixedEndpoint, id), format, cancellationToken);
            if (!plan.AlsoSpeakers) return call;
            try { return new MirroredPlaybackDevice(call, speakers.Open(selection, format, cancellationToken), format.BlockAlignment); }
            // The usual output failing never stops Martlet's voice in the call.
            catch (ContractException) { return call; }
        }
    }

    // ---------- the camera view ----------

    /// <summary>The camera view is open (the character in its own 16:9 window on a solid background or picture for OBS).</summary>
    internal bool CameraOpen { get; set; }

    /// <summary>The camera view as the saved background shows it: its color and, for a picture, the picture's file, and how the
    /// character is framed in it.</summary>
    internal Martlet.Avatar.Hosting.RendererCamera CameraView
    {
        get
        {
            var saved = Preferences;
            return new(true, saved.CameraColor, saved.CameraBackground == DiscordCameraBackground.Picture && DiscordCallPreferences.HasPicture(directory)
                ? DiscordCallPreferences.PicturePath(directory!) : null, saved.CameraZoom, saved.CameraX, saved.CameraY);
        }
    }

    /// <summary>Keeps <paramref name="bytes"/> as the camera picture and switches the background to it. Returns why it can't, or
    /// null.</summary>
    internal string? UsePicture(byte[] bytes, DiscordCameraPictureSource source)
    {
        if (DiscordCallPreferences.SavePicture(directory, bytes) is { } problem) return problem;
        return Save(prefs => prefs with { CameraBackground = DiscordCameraBackground.Picture, CameraPicture = source })
            ? null : "Couldn't save this choice.";
    }

    internal bool HasPicture => DiscordCallPreferences.HasPicture(directory);

    // ---------- status ----------

    /// <summary>The call card's one-line status: whether the mode is on and how Martlet hears, sees and speaks.</summary>
    internal string Status(bool listening)
    {
        var saved = Preferences;
        if (!saved.On) return "Off. Martlet isn't in your Discord calls.";
        var hears = Capturing switch
        {
            "discord" => "hears the Discord app",
            "system" => "hears everything this PC plays except itself",
            _ => saved.Capture == DiscordCallCapture.DiscordApp ? "will hear the Discord app" : "will hear everything this PC plays"
        };
        var plan = Plan;
        return $"On. Martlet {hears}" + (listening ? "" : " once always listening runs") +
            $", sees who talks: {Attribution.Source}, and speaks into {plan.Summary.TrimEnd('.')}.";
    }
}

/// <summary>One picture of the Discord window (BGRA32), for telling who is speaking; never kept or sent.</summary>
internal sealed record CallWindowSample(byte[] Pixels, int Width, int Height);

/// <summary>Who is speaking in the call, from the Discord window: while the call is heard (the PC listener hears someone) a
/// picture of the Discord window is taken at most every <see cref="Interval"/> (at most <see cref="MaximumSamples"/> per
/// utterance) and each lit speaking indicator's name is read on this PC; the name lit most often names that utterance. The
/// pictures stay on this PC, are cleared after reading and never reach Thinking, memory or any provider.</summary>
internal sealed class DiscordCallAttribution(Func<DiscordCallPreferences> preferences,
    Func<DiscordCallPreferences, CallWindowSample?> look, ICallTextReader reader)
{
    internal static TimeSpan Interval => TimeSpan.FromMilliseconds(450);
    internal const int MaximumSamples = 6;
    private static readonly TimeSpan Stale = TimeSpan.FromSeconds(30);
    private readonly Lock gate = new();
    private readonly DiscordSpeakerReader names = new(reader);
    private readonly Queue<(long End, DiscordSpeakerVote Vote)> spans = new();
    private readonly HashSet<string> attributed = new(StringComparer.OrdinalIgnoreCase);
    private DiscordSpeakerVote? current;
    private Task? sampling;
    private long lastSample;
    private string source = "not yet";

    /// <summary>How speakers are told apart now: "the Discord window", "the Discord window (nobody lit)", "off" or why not.</summary>
    internal string Source { get { lock (gate) return preferences().SeeSpeakers ? source : "off"; } }

    /// <summary>How many different people were named in this conversation.</summary>
    internal int Attributed { get { lock (gate) return attributed.Count; } }

    /// <summary>Called every UI tick while the call mode is on: <paramref name="hearing"/> is whether the call's listener hears
    /// someone right now.</summary>
    internal void Tick(bool hearing, TimeProvider clock)
    {
        var now = clock.GetTimestamp();
        DiscordCallPreferences saved;
        lock (gate)
        {
            saved = preferences();
            if (hearing && current is null) current = new();
            if (!hearing && current is { } finished)
            {
                spans.Enqueue((now, finished));
                current = null;
            }
            while (spans.Count > 0 && clock.GetElapsedTime(spans.Peek().End) > Stale) spans.Dequeue();
            if (!saved.SeeSpeakers || current is not { } vote || vote.Samples >= MaximumSamples || sampling is { IsCompleted: false } ||
                lastSample != 0 && clock.GetElapsedTime(lastSample) < Interval) return;
            lastSample = now;
            sampling = Task.Run(() => SampleAsync(vote, saved));
        }
    }

    private async Task SampleAsync(DiscordSpeakerVote vote, DiscordCallPreferences saved)
    {
        CallWindowSample? picture = null;
        try
        {
            picture = look(saved);
            if (picture is null)
            {
                lock (gate) source = "the Discord window isn't showing";
                return;
            }
            var speaking = await names.SpeakingAsync(picture.Pixels, picture.Width, picture.Height, saved.OwnerName, CancellationToken.None)
                .ConfigureAwait(false);
            lock (gate)
            {
                vote.Add(speaking);
                source = "the Discord window";
            }
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or ExternalException or OutOfMemoryException)
        {
            lock (gate) source = "the Discord window can't be read";
        }
        finally { if (picture is not null) Array.Clear(picture.Pixels); }
    }

    /// <summary>Who spoke the oldest utterance not yet named (null: someone).</summary>
    internal string? Take()
    {
        lock (gate)
        {
            if (spans.Count == 0) return current?.Winner;
            var name = spans.Dequeue().Vote.Winner;
            if (name is not null) attributed.Add(name);
            return name;
        }
    }

    /// <summary>Forgets what was heard (the call ended or listening stopped).</summary>
    internal void Reset()
    {
        lock (gate)
        {
            spans.Clear();
            current = null;
        }
    }

    /// <summary>One picture run through the production detector and reader, for MCP's simulated call.</summary>
    internal Task<IReadOnlyList<string>> ReadAsync(CallWindowSample picture, string? ownerName, CancellationToken token) =>
        names.SpeakingAsync(picture.Pixels, picture.Width, picture.Height, ownerName, token);
}

/// <summary>Plays the same voice on two outputs: the call's (the clock everything follows) and the owner's usual output, which
/// takes what fits in its buffer and never holds the call back.</summary>
internal sealed class MirroredPlaybackDevice(IPlaybackDevice call, IPlaybackDevice own, int alignment) : IPlaybackDevice, IPlaybackClockDevice
{
    private bool ownFailed;
    public PlaybackDeviceInfo Info => call.Info;
    public int GetPadding(CancellationToken cancellationToken) => call.GetPadding(cancellationToken);

    public int Write(ReadOnlySpan<byte> pcm, CancellationToken cancellationToken)
    {
        var written = call.Write(pcm, cancellationToken);
        if (!ownFailed && written > 0)
        {
            try
            {
                var room = own.Info.BufferCapacitySamples - own.GetPadding(cancellationToken);
                var samples = Math.Min(written, room);
                if (samples > 0) own.Write(pcm[..(samples * alignment)], cancellationToken);
            }
            catch (ContractException) { ownFailed = true; }
        }
        return written;
    }

    public void Start(CancellationToken cancellationToken)
    {
        call.Start(cancellationToken);
        try { if (!ownFailed) own.Start(cancellationToken); }
        catch (ContractException) { ownFailed = true; }
    }

    public void StopAndReset()
    {
        try { own.StopAndReset(); }
        catch (ContractException) { }
        call.StopAndReset();
    }

    public DeviceClockReading? ReadClock(CancellationToken cancellationToken) =>
        call is IPlaybackClockDevice clock ? clock.ReadClock(cancellationToken) : null;

    public void Dispose()
    {
        try { own.Dispose(); }
        finally { call.Dispose(); }
    }
}

/// <summary>The Discord app on this PC, found by its process name (Discord, DiscordPTB or DiscordCanary), and a picture of its
/// main window taken without touching it (PrintWindow's full-content rendering, never input).</summary>
internal static class DiscordWindow
{
    private static readonly string[] Names = ["Discord", "DiscordPTB", "DiscordCanary"];
    private const int MaximumEdge = 3840;

    /// <summary>Discord's main process (the one with its window, else the oldest), or null when Discord isn't running.</summary>
    internal static int? Process() => MainProcess()?.Id;

    /// <summary>Discord's main window, or 0.</summary>
    internal static nint Window() => MainProcess()?.Window ?? 0;

    private static (int Id, nint Window)? MainProcess()
    {
        (int Id, nint Window, DateTime Started)? best = null;
        foreach (var name in Names)
        {
            foreach (var process in System.Diagnostics.Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        var window = process.MainWindowHandle;
                        var started = process.StartTime;
                        if (best is null || window != 0 && best.Value.Window == 0 ||
                            (window != 0) == (best.Value.Window != 0) && started < best.Value.Started)
                            best = (process.Id, window, started);
                    }
                    catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                }
            }
        }
        return best is { } found ? (found.Id, found.Window) : null;
    }

    /// <summary>A picture of the Discord window, or null while Discord isn't running, is minimized or can't be drawn.</summary>
    internal static CallWindowSample? Look(DiscordCallPreferences _)
    {
        var window = Window();
        if (window == 0 || IsIconic(window) || !GetWindowRect(window, out var rect)) return null;
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        if (width < 64 || height < 64 || width > MaximumEdge || height > MaximumEdge) return null;
        var screen = GetDC(0);
        if (screen == 0) return null;
        nint memory = 0, bitmap = 0, previous = 0;
        try
        {
            memory = CreateCompatibleDC(screen);
            var header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32
            };
            bitmap = CreateDIBSection(screen, ref header, 0, out var bits, 0, 0);
            if (memory == 0 || bitmap == 0 || bits == 0) return null;
            previous = SelectObject(memory, bitmap);
            if (!PrintWindow(window, memory, RenderFullContent)) return null;
            GdiFlush();
            var pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return new(pixels, width, height);
        }
        finally
        {
            if (previous != 0) SelectObject(memory, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    private const uint RenderFullContent = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant;
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PrintWindow(nint window, nint dc, uint flags);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfoHeader header, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
}

/// <summary>Whether this PC is ready for Martlet in your Discord calls, read without recording or playing anything: Windows can
/// hear one app alone (a process loopback set up and closed unstarted), Discord runs, its window shows, Windows' OCR has a
/// language, and the chosen output (or a virtual cable) is connected.</summary>
internal sealed record DiscordCallDoctor(bool AppLoopback, string? AppLoopbackProblem, bool DiscordRunning, bool DiscordWindowShown,
    bool TextReading, bool OutputPresent, string? OutputName, string? VirtualCable)
{
    internal static DiscordCallDoctor Check(DiscordCallPreferences preferences, IReadOnlyList<CallOutput>? outputs)
    {
        var probe = WasapiPcAudioSourceFactory.ProbeApp();
        var plan = DiscordCallOutputs.Plan(preferences, outputs);
        return new(probe.WithoutMartlet, probe.Problem, DiscordWindow.Process() is not null, DiscordWindow.Window() != 0,
            WindowsCallTextReader.Available, plan.Present, plan.OutputName,
            outputs is null ? null : DiscordCallOutputs.Suggest(outputs)?.Name);
    }

    internal string Describe() =>
        (AppLoopback ? "Windows can hear the Discord app alone." : $"Windows can't hear one app alone ({AppLoopbackProblem}), so Martlet hears everything but itself.") +
        (DiscordRunning ? DiscordWindowShown ? " Discord is running." : " Discord is running in the background." : " Discord isn't running.") +
        (TextReading ? "" : " Windows has no text-reading (OCR) language, so speakers can't be named.") +
        (OutputName is null ? VirtualCable is { } cable ? $" {cable} is available for Martlet's voice." : " No virtual cable found."
            : OutputPresent ? $" {OutputName} is connected." : $" {OutputName} isn't connected.");
}
