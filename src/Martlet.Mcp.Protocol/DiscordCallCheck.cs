using System.Diagnostics;
using System.Globalization;
using System.IO;
using Martlet.Audio;
using Martlet.Audio.Windows;
using Martlet.Core.Settings;
using Martlet.Discord.Calls;
using NAudio.CoreAudioApi;

namespace Martlet.Mcp;

/// <summary>discord_call_check: Martlet in your own Discord calls (companion mode). Reads the saved mode
/// (discord-calls.json), checks this PC without recording or playing anything (a process loopback of one app is set up and
/// closed unstarted; Discord's process and window; Windows' OCR language; the playback devices and whether the chosen output or
/// a virtual cable is there), then runs a simulated call utterance through the production path: a fixture call voice through
/// PcAudioCaptureFactory, MicrophoneCapture and the voice-activity detector on a simulated clock (two utterances), fixture
/// pictures of the Discord window (member list, call grid, the owner's own tile) through the production speaking detector
/// and Windows' real OCR on this PC, the call lines and the In your Discord call prompt as the talk window builds them, and a
/// fixture reply (NOT AI) that answers when Martlet's name is said and otherwise passes.</summary>
internal static class DiscordCallCheck
{
    private const string Marker = "[PC audio]";
    private const string Silent = "pass";
    private static readonly string[] Names = ["Martlet", "Jane"];

    internal static async Task<object> RunAsync(string dataDirectory, CancellationToken cancellation)
    {
        var saved = DiscordCallPreferences.Load(dataDirectory);
        var outputs = Outputs();
        var discord = DiscordProcess();
        var probe = WasapiPcAudioSourceFactory.ProbeApp(discord?.Id, cancellation);
        var plan = DiscordCallOutputs.Plan(saved, outputs);
        var ocr = WindowsCallTextReader.Available;

        // The call's sound: a fixture loopback (like Discord's process loopback) on a simulated clock.
        var watch = Stopwatch.StartNew();
        var clock = new PcAudioCheck.SimulatedClock();
        var devices = new PcAudioCaptureFactory(new PcAudioCheck.FixtureSources(clock), clock);
        var pcm = await PcAudioCheck.RecordAsync(devices, cancellation);
        var segments = PcAudioCheck.Segments(pcm);
        var audioMs = watch.ElapsedMilliseconds;

        // Who spoke: fixture pictures of the Discord window through the production detector and Windows' OCR.
        var reader = new DiscordSpeakerReader(new WindowsCallTextReader());
        var scenes = new List<object>();
        var named = new List<string?>();
        foreach (var (scene, expected, draw) in Scenes())
        {
            var (pixels, width, height) = await RenderAsync(draw);
            watch.Restart();
            var marks = DiscordSpeakingDetector.Find(pixels, width, height);
            var green = 0;
            for (var i = 0; i < width * height; i++)
                if (DiscordSpeakingDetector.IsSpeakingGreen(pixels[i * 4], pixels[i * 4 + 1], pixels[i * 4 + 2])) green++;
            var vote = new DiscordSpeakerVote();
            vote.Add(await reader.SpeakingAsync(pixels, width, height, "Ben", cancellation));
            Array.Clear(pixels);
            named.Add(vote.Winner);
            scenes.Add(new
            {
                scene, speakingGreenPixels = green, marks = marks.Select(mark => mark.Kind.ToString()).ToArray(), expected, named = vote.Winner,
                ok = string.Equals(expected, vote.Winner, StringComparison.OrdinalIgnoreCase), elapsedMs = watch.ElapsedMilliseconds
            });
        }

        // What was said (simulated speech-to-text, NOT a real transcription) by whom, as the talk window sends it.
        string[] words = ["Hey Jane, are you coming to the game tonight?", "I think the boss fight is next."];
        var heard = words.Select((text, i) => (Text: text, Speaker: i < named.Count ? named[i] : null)).ToArray();
        var lines = heard.Select(line => Marker + " " + DiscordCallLine.Format(line.Speaker, line.Text)).ToArray();
        var turns = heard.Select(line =>
        {
            var addressed = DiscordCallLine.Addressed(line.Text, Names);
            // The fixture reply engine (NOT AI): answers when Martlet's name is said, otherwise passes.
            var reply = addressed ? $"Hi {line.Speaker ?? "there"}! I wouldn't miss it." : $"[{Silent}]";
            return new { line = Marker + " " + DiscordCallLine.Format(line.Speaker, line.Text), addressed, answersAtOnce = addressed, reply };
        }).ToArray();
        var prompt = PromptSettings.Fill(null, PromptCatalog.DiscordCall, ("marker", Marker), ("silent", Silent));

        var audioOk = segments.Count == 2;
        var namesOk = scenes.Count == 3 && named.SequenceEqual(["Alice", "Bob", null], StringComparer.OrdinalIgnoreCase);
        var replyOk = turns[0].addressed && !turns[1].addressed && turns[1].reply == $"[{Silent}]";
        return new
        {
            ok = audioOk && (namesOk || !ocr) && replyOk,
            saved = new
            {
                on = saved.On, capture = saved.Capture.ToString(), seeSpeakers = saved.SeeSpeakers, ownerNameSet = saved.OwnerName is not null,
                output = plan.OutputName, alsoSpeakers = saved.AlsoSpeakers, bargeIn = saved.BargeIn,
                cameraBackground = saved.CameraBackground.ToString(),
                cameraPicture = saved.CameraPicture,
                cameraPictureHere = saved.CameraPicture is null ? (bool?)null
                    : Martlet.Conversation.PictureCreations.Find(dataDirectory, saved.CameraPicture) is { Removed: false } picture &&
                      Martlet.Core.Creations.CreationStore.IsComplete(dataDirectory, picture),
                cameraTool = saved.On
                    ? "set_camera_background (color, picture or draw) is offered to every tool-capable reply while the mode is on"
                    : "set_camera_background isn't offered while the mode is off"
            },
            doctor = new
            {
                appLoopback = probe.WithoutMartlet,
                appLoopbackProblem = probe.Problem,
                probedProcess = discord is null ? "this MCP server (Discord isn't running)" : "Discord",
                recorded = false,
                discordRunning = discord is not null,
                discordWindowShown = discord?.Window == true,
                textReading = ocr,
                outputs = outputs?.Count,
                virtualCable = outputs is null ? null : DiscordCallOutputs.Suggest(outputs)?.Name,
                chosenOutputPresent = plan.Present,
                voiceGoesTo = plan.Summary
            },
            simulation = new
            {
                audio = new
                {
                    scene = "fixture call voice on a simulated clock (nothing recorded or played): talking 0-3 s, silence 3-6 s, talking 6-9 s",
                    utterances = segments.Select(s => new { startS = s.Start, endS = s.End }).ToArray(),
                    ok = audioOk,
                    elapsedMs = audioMs
                },
                attribution = new
                {
                    how = "fixture pictures of the Discord window (drawn here with GDI, never shown, kept or sent) through the production " +
                        "speaking detector and Windows' own OCR on this PC; the owner (Ben) is left out",
                    ok = namesOk,
                    scenes
                },
                message = string.Join("\n", lines),
                turns,
                prompt,
                reply = "fixture reply engine (NOT AI): answers when Martlet's name is said, otherwise passes"
            }
        };
    }

    private sealed record Discord(int Id, bool Window);

    private static Discord? DiscordProcess()
    {
        Discord? found = null;
        foreach (var name in new[] { "Discord", "DiscordPTB", "DiscordCanary" })
            foreach (var process in Process.GetProcessesByName(name))
                using (process)
                {
                    try
                    {
                        var window = process.MainWindowHandle != 0;
                        if (found is null || window && !found.Window) found = new(process.Id, window);
                    }
                    catch (InvalidOperationException) { }
                }
        return found;
    }

    // The playback devices' names only (nothing is opened); null when Windows didn't say.
    private static IReadOnlyList<CallOutput>? Outputs()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultId = null;
            if (enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Console))
                using (var current = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console)) defaultId = current.ID;
            var list = new List<CallOutput>();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                using (device) list.Add(new(device.ID, device.FriendlyName, device.ID == defaultId));
            return list;
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException) { return null; }
    }

    private static readonly (byte R, byte G, byte B) DarkGrey = (49, 51, 56), Tile = (30, 31, 34), Speaking = (35, 165, 90),
        Text = (219, 222, 225), Pill = (17, 18, 20);

    // Discord's dark theme as drawn here: a voice channel's member list (Alice lit), the call grid (Bob's tile lit) and the
    // member list with only the owner (Ben) lit.
    private static IEnumerable<(string Scene, string? Expected, Action<FixturePicture> Draw)> Scenes()
    {
        yield return ("voice channel member list, Alice speaking", "Alice", picture => MemberList(picture, 0));
        yield return ("call grid, Bob's tile speaking", "Bob", picture =>
        {
            picture.Fill(0, 0, picture.Width, picture.Height, DarkGrey);
            string[] people = ["Alice", "Bob", "Ben"];
            for (var i = 0; i < 3; i++)
            {
                int left = 16 + i * 312, top = 120;
                picture.Fill(left, top, 300, 200, Tile);
                if (i == 1) picture.Border(left, top, 300, 200, 3, Speaking);
                picture.Disc(left + 150, top + 90, 40, (88, 101, 242));
                picture.Fill(left + 8, top + 200 - 34, 70, 26, Pill);
                picture.Text(left + 14, top + 200 - 32, people[i], 18, Text);
            }
        });
        yield return ("member list, only you (Ben) speaking", null, picture => MemberList(picture, 2));
    }

    private static void MemberList(FixturePicture picture, int speaking)
    {
        picture.Fill(0, 0, picture.Width, picture.Height, DarkGrey);
        picture.Text(24, 14, "GENERAL", 15, (148, 155, 164));
        string[] people = ["Alice", "Bob", "Ben"];
        for (var i = 0; i < 3; i++)
        {
            int x = 48, y = 56 + i * 34;
            picture.Disc(x, y, 11, ((byte)(80 + i * 60), 101, 200));
            if (i == speaking) picture.Ring(x, y, 14, 3, Speaking);
            picture.Text(70, y - 11, people[i], 20, Text);
        }
    }

    private static Task<(byte[] Pixels, int Width, int Height)> RenderAsync(Action<FixturePicture> draw) => Task.Run(() =>
    {
        using var picture = new FixturePicture(960, 540);
        draw(picture);
        return (picture.Pixels(), picture.Width, picture.Height);
    });

    /// <summary>A BGRA32 picture drawn with GDI into memory (works on a locked or headless desktop, unlike WPF's renderer).</summary>
    private sealed class FixturePicture : IDisposable
    {
        private readonly nint dc, bitmap, previous, bits;
        public int Width { get; }
        public int Height { get; }

        public FixturePicture(int width, int height)
        {
            Width = width;
            Height = height;
            dc = CreateCompatibleDC(0);
            var header = new BitmapInfoHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            bitmap = CreateDIBSection(dc, ref header, 0, out bits, 0, 0);
            if (dc == 0 || bitmap == 0 || bits == 0) throw new InvalidOperationException("GDI couldn't make the fixture picture.");
            previous = SelectObject(dc, bitmap);
        }

        public void Fill(int left, int top, int width, int height, (byte R, byte G, byte B) color)
        {
            var brush = CreateSolidBrush(Rgb(color));
            var rect = new NativeRect { Left = left, Top = top, Right = left + width, Bottom = top + height };
            FillRect(dc, ref rect, brush);
            DeleteObject(brush);
        }

        public void Border(int left, int top, int width, int height, int thickness, (byte, byte, byte) color)
        {
            Fill(left, top, width, thickness, color);
            Fill(left, top + height - thickness, width, thickness, color);
            Fill(left, top, thickness, height, color);
            Fill(left + width - thickness, top, thickness, height, color);
        }

        public void Disc(int x, int y, int radius, (byte R, byte G, byte B) color)
        {
            var brush = CreateSolidBrush(Rgb(color));
            var pen = CreatePen(0, 1, Rgb(color));
            var oldBrush = SelectObject(dc, brush);
            var oldPen = SelectObject(dc, pen);
            Ellipse(dc, x - radius, y - radius, x + radius + 1, y + radius + 1);
            SelectObject(dc, oldBrush);
            SelectObject(dc, oldPen);
            DeleteObject(brush);
            DeleteObject(pen);
        }

        public void Ring(int x, int y, int radius, int thickness, (byte R, byte G, byte B) color)
        {
            var pen = CreatePen(0, thickness, Rgb(color));
            var oldPen = SelectObject(dc, pen);
            var oldBrush = SelectObject(dc, GetStockObject(5));
            var r = radius - thickness / 2;
            Ellipse(dc, x - r, y - r, x + r + 1, y + r + 1);
            SelectObject(dc, oldBrush);
            SelectObject(dc, oldPen);
            DeleteObject(pen);
        }

        public void Text(int x, int y, string text, int size, (byte R, byte G, byte B) color)
        {
            var font = CreateFontW(-size, 0, 0, 0, 500, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI");
            var oldFont = SelectObject(dc, font);
            SetTextColor(dc, Rgb(color));
            SetBkMode(dc, 1);
            TextOutW(dc, x, y, text, text.Length);
            SelectObject(dc, oldFont);
            DeleteObject(font);
        }

        public byte[] Pixels()
        {
            GdiFlush();
            var pixels = new byte[Width * Height * 4];
            System.Runtime.InteropServices.Marshal.Copy(bits, pixels, 0, pixels.Length);
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            return pixels;
        }

        public void Dispose()
        {
            SelectObject(dc, previous);
            DeleteObject(bitmap);
            DeleteDC(dc);
        }

        private static uint Rgb((byte R, byte G, byte B) color) => (uint)(color.R | color.G << 8 | color.B << 16);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint Size; public int Width, Height; public ushort Planes, BitCount;
            public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant;
        }

        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfoHeader header, uint usage, out nint bits, nint section, uint offset);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint color);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern nint CreatePen(int style, int width, uint color);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern nint GetStockObject(int index);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool Ellipse(nint dc, int left, int top, int right, int bottom);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int FillRect(nint dc, ref NativeRect rect, nint brush);
        [System.Runtime.InteropServices.DllImport("gdi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline,
            uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern uint SetTextColor(nint dc, uint color);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern int SetBkMode(nint dc, int mode);
        [System.Runtime.InteropServices.DllImport("gdi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool TextOutW(nint dc, int x, int y, string text, int length);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool GdiFlush();
    }
}