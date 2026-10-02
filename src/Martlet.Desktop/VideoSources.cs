using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Martlet.Desktop;

/// <summary>What Watch looks at: the user's screen, a camera Windows knows (webcam, capture card, virtual camera, a
/// phone connected as a webcam) or a phone / network camera address.</summary>
internal enum WatchKind { ActiveWindow, ActiveScreen, Camera, Url }

/// <summary>One video source. <paramref name="Id"/> is the camera's Windows device link or the address;
/// <paramref name="Name"/> is what the user and the model see (never the address, which may hold a password).</summary>
internal sealed record WatchSource(WatchKind Kind, string Id = "", string Name = "")
{
    internal bool IsScreen => Kind is WatchKind.ActiveWindow or WatchKind.ActiveScreen;
    internal ScreenScope Scope => Kind == WatchKind.ActiveScreen ? ScreenScope.ActiveScreen : ScreenScope.ActiveWindow;

    /// <summary>A short label for status text and the commentary prompt.</summary>
    internal string Label => Kind switch
    {
        WatchKind.ActiveWindow => "your active window",
        WatchKind.ActiveScreen => "your screen",
        WatchKind.Camera => Name.Length > 0 ? $"the camera \"{Name}\"" : "your camera",
        _ => Name.Length > 0 ? $"the camera at {Name}" : "your phone or network camera"
    };

    /// <summary>The host (and port) of an address, without user name, password, path or query; for a Home Assistant
    /// camera snapshot address, the camera's entity name ("front door").</summary>
    internal static string SafeName(string address) =>
        Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) && !uri.IsFile
            ? IsHomeAssistantCamera(uri) ? uri.Segments[^1].Replace("camera.", "", StringComparison.Ordinal).Replace('_', ' ')
            : uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}"
        : Path.GetFileName(address.Trim().Trim('"'));

    /// <summary>A Home Assistant camera snapshot address: <c>.../api/camera_proxy/camera.front_door</c>.</summary>
    internal static bool IsHomeAssistantCamera(Uri uri) =>
        uri.Scheme is "http" or "https" && uri.Segments.Length >= 3 &&
        string.Equals(uri.Segments[^2], "camera_proxy/", StringComparison.Ordinal) &&
        uri.Segments[^1].StartsWith("camera.", StringComparison.Ordinal) && uri.Segments[^1].Length > "camera.".Length;

    /// <summary>The address with any user name and password removed, safe to save in preferences.</summary>
    internal static string WithoutCredentials(string address) =>
        Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) && uri.UserInfo.Length > 0
            ? new UriBuilder(uri) { UserName = "", Password = "" }.Uri.ToString() : address.Trim();

    /// <summary>A typed camera address as Martlet reads it: quotes trimmed, and http:// added when no scheme or file is given.</summary>
    internal static string Normalize(string? text)
    {
        var address = (text ?? "").Trim().Trim('"');
        if (address.Length == 0 || address.Contains("://", StringComparison.Ordinal) || File.Exists(address)) return address;
        return "http://" + address;
    }
}

internal sealed record CameraDevice(string Id, string Name);

internal sealed class VideoSourceException(string message) : Exception(message);

internal interface IVideoInput
{
    /// <summary>Cameras Windows offers to desktop apps. Only called when the user asks for the list.</summary>
    IReadOnlyList<CameraDevice> Cameras();
    /// <summary>One downscaled look at a camera or address; a failure comes back as CaptureFailed with a plain Note.</summary>
    GlanceResult Capture(WatchSource source);
    /// <summary>Closes the open camera or stream (its light goes off) when watching stops.</summary>
    void Release();
}

/// <summary>Reads cameras and video addresses for Watch. Cameras (and rtsp:// streams or video files) go through Windows
/// Media Foundation and stay open only while watching, so the camera light is on exactly then. http(s) addresses are
/// fetched as one JPEG/PNG snapshot, or the first picture of an MJPEG stream, per look (the common phone IP-camera
/// apps serve both). Frames live only in memory, downscaled to at most <see cref="ScreenGlancer.MaximumEdge"/> px.</summary>
internal sealed class VideoInput : IVideoInput
{
    private const int MaximumDownload = 12 * 1024 * 1024;
    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromSeconds(8)
    };
    private readonly Lock gate = new();
    private readonly Func<Uri, AuthenticationHeaderValue?>? authorize;
    private MediaReader? reader;
    private string? openKey;
    private bool mediaAddress;
    private byte[]? previous;

    /// <param name="authorize">Supplies a saved token for addresses that need one (Home Assistant camera snapshots);
    /// the token is never part of the address or saved with it.</param>
    internal VideoInput(Func<Uri, AuthenticationHeaderValue?>? authorize = null) => this.authorize = authorize;

    public IReadOnlyList<CameraDevice> Cameras() => MediaReader.Cameras();

    public GlanceResult Capture(WatchSource source)
    {
        lock (gate)
        {
            var key = source.Kind + "|" + source.Id;
            if (openKey != key) ReleaseLocked();
            openKey = key;
            try
            {
                var frame = source.Kind switch
                {
                    WatchKind.Camera => (reader ??= MediaReader.OpenCamera(source.Id)).Read(),
                    WatchKind.Url => ReadAddress(source.Id),
                    _ => throw new VideoSourceException("This is not a camera source.")
                };
                var (pixels, width, height) = frame;
                var signature = ScreenGlancer.Signature(pixels, width, height);
                if (signature.Max() < 10)
                {
                    Array.Clear(pixels);
                    return new(null, GlanceSkip.Blank);
                }
                var change = previous is null ? 1.0 : signature.Zip(previous, (a, b) => Math.Abs(a - b)).Average() / 255.0;
                previous = signature;
                return new(new(pixels, width, height, source.Name, change), GlanceSkip.None);
            }
            catch (VideoSourceException error)
            {
                ReleaseLocked();
                openKey = key;
                return new(null, GlanceSkip.CaptureFailed, error.Message);
            }
            catch (Exception error) when (error is ExternalException or OutOfMemoryException or ArgumentException or InvalidOperationException
                or NotSupportedException or IOException or HttpRequestException or TaskCanceledException or FileFormatException)
            {
                ReleaseLocked();
                openKey = key;
                return new(null, GlanceSkip.CaptureFailed, Describe(error, source));
            }
        }
    }

    public void Release()
    {
        lock (gate)
        {
            ReleaseLocked();
            openKey = null;
        }
    }

    private void ReleaseLocked()
    {
        reader?.Dispose();
        reader = null;
        mediaAddress = false;
        previous = null;
    }

    private (byte[] Pixels, int Width, int Height) ReadAddress(string address)
    {
        var text = address.Trim().Trim('"');
        if (text.Length == 0) throw new VideoSourceException("Type the camera's address first (for example http://192.168.1.20:8080/shot.jpg).");
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && !mediaAddress)
        {
            var picture = Snapshot(uri);
            if (picture is not null) return picture.Value;
            mediaAddress = true; // The address serves a video file or stream: hand it to Media Foundation.
        }
        return (reader ??= MediaReader.OpenAddress(text)).Read();
    }

    // One JPEG/PNG (or the first part of a multipart MJPEG stream); null when the address serves video instead.
    private (byte[] Pixels, int Width, int Height)? Snapshot(Uri uri)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri.UserInfo.Length > 0 ? new UriBuilder(uri) { UserName = "", Password = "" }.Uri : uri);
        if (uri.UserInfo.Length > 0)
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.UnescapeDataString(uri.UserInfo))));
        else if (authorize?.Invoke(uri) is { } saved)
            request.Headers.Authorization = saved;
        using var response = Http.Send(request, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden &&
            WatchSource.IsHomeAssistantCamera(uri))
            throw new VideoSourceException("Home Assistant refused the camera picture. Connect Home Assistant in Companion > Smart home " +
                "with this same address, then try again.");
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new VideoSourceException("The camera asked for a password. Put it in the address as http://user:password@host:port/... " +
                "(the password stays in this window and is never saved).");
        if (!response.IsSuccessStatusCode)
            throw new VideoSourceException($"The camera address answered {(int)response.StatusCode} {response.ReasonPhrase}. Check the path (for example /shot.jpg).");
        var type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
        if (type.StartsWith("video/") || type.Contains("mpegurl") || type == "application/octet-stream" && LooksLikeVideo(uri)) return null;
        if (type.StartsWith("text/html"))
            throw new VideoSourceException("That address is a web page, not a picture. Use the camera app's snapshot (.jpg) or MJPEG video address.");
        using var stream = response.Content.ReadAsStream();
        var bytes = type.StartsWith("multipart/") ? FirstJpeg(stream) : ReadAll(stream);
        try { return Decode(bytes); }
        finally { Array.Clear(bytes); }
    }

    private static bool LooksLikeVideo(Uri uri) =>
        Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() is ".mp4" or ".m4v" or ".mov" or ".mkv" or ".wmv" or ".avi" or ".ts" or ".m3u8";

    private static byte[] ReadAll(Stream stream)
    {
        using var copy = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            if (copy.Length + read > MaximumDownload) throw new VideoSourceException("The picture at that address is too large (over 12 MB).");
            copy.Write(buffer, 0, read);
        }
        return copy.ToArray();
    }

    // Scans an MJPEG (multipart/x-mixed-replace) stream for the first complete JPEG, then hangs up.
    internal static byte[] FirstJpeg(Stream stream)
    {
        using var copy = new MemoryStream();
        var buffer = new byte[32 * 1024];
        int start = -1, scanned = 0, read;
        while ((read = stream.Read(buffer)) > 0)
        {
            copy.Write(buffer, 0, read);
            var data = copy.GetBuffer();
            var length = (int)copy.Length;
            for (var i = Math.Max(1, scanned); i < length; i++)
            {
                if (start < 0 && data[i - 1] == 0xFF && data[i] == 0xD8) start = i - 1;
                else if (start >= 0 && data[i - 1] == 0xFF && data[i] == 0xD9) return data.AsSpan(start, i + 1 - start).ToArray();
            }
            scanned = length;
            if (length > MaximumDownload) break;
        }
        throw new VideoSourceException("The stream at that address didn't contain a JPEG picture. Use an MJPEG or snapshot (.jpg) address.");
    }

    internal static (byte[] Pixels, int Width, int Height) Decode(byte[] bytes)
    {
        using var input = new MemoryStream(bytes, writable: false);
        BitmapSource frame;
        try
        {
            frame = BitmapDecoder.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        }
        catch (Exception error) when (error is NotSupportedException or FileFormatException or ArgumentException or ExternalException)
        {
            throw new VideoSourceException("The address didn't return a picture Windows can read (JPEG or PNG). Use the camera app's snapshot or MJPEG address.");
        }
        var scale = Math.Min(1.0, (double)ScreenGlancer.MaximumEdge / Math.Max(frame.PixelWidth, frame.PixelHeight));
        if (scale < 1.0) frame = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgr32, null, 0);
        int width = converted.PixelWidth, height = converted.PixelHeight;
        var pixels = new byte[width * height * 4];
        converted.CopyPixels(pixels, width * 4, 0);
        return (pixels, width, height);
    }

    private static string Describe(Exception error, WatchSource source)
    {
        if (error is TaskCanceledException) return "The camera address didn't answer in time. Check that the phone and this PC are on the same network and the camera app is running.";
        if (error is HttpRequestException http)
            return $"Couldn't reach the camera address ({http.HttpRequestError}). Check the address, that the camera app is running, and that the phone is on the same network.";
        var code = error is ExternalException external ? external.HResult : error.HResult;
        return MediaReader.Explain(code, source.Kind == WatchKind.Camera);
    }
}

/// <summary>A Media Foundation Source Reader that delivers RGB32 frames from a camera, a stream address or a video file,
/// through raw COM vtable calls (no extra packages). Live sources are flushed before each read so a look is current;
/// files play along with the wall clock and loop.</summary>
internal sealed class MediaReader : IDisposable
{
    private static readonly Lock StartGate = new();
    private static bool started;
    private nint reader, activate;
    private readonly int width, height, stride;
    private readonly long duration;
    private readonly long opened = Environment.TickCount64;

    private MediaReader(nint reader, nint activate)
    {
        this.reader = reader;
        this.activate = activate;
        try
        {
            Check(Call<SetStreamSelection>(reader, 4)(reader, AllStreams, 0));
            Check(Call<SetStreamSelection>(reader, 4)(reader, FirstVideoStream, 1));
            Check(MFCreateMediaType(out var type));
            try
            {
                var major = MediaTypeVideo; var subtype = VideoFormatRgb32;
                var majorKey = MtMajorType; var subtypeKey = MtSubtype;
                Check(Call<SetGuid>(type, 24)(type, ref majorKey, ref major));
                Check(Call<SetGuid>(type, 24)(type, ref subtypeKey, ref subtype));
                Check(Call<SetCurrentMediaType>(reader, 7)(reader, FirstVideoStream, 0, type));
            }
            finally { Marshal.Release(type); }
            Check(Call<GetCurrentMediaType>(reader, 6)(reader, FirstVideoStream, out var current));
            try
            {
                var sizeKey = MtFrameSize; var strideKey = MtDefaultStride;
                Check(Call<GetUInt64>(current, 8)(current, ref sizeKey, out var size));
                width = (int)(size >> 32);
                height = (int)(size & 0xFFFFFFFF);
                stride = Call<GetUInt32>(current, 7)(current, ref strideKey, out var value) >= 0 ? unchecked((int)value) : width * 4;
            }
            finally { Marshal.Release(current); }
            if (width <= 0 || height <= 0 || Math.Abs(stride) < width * 4) throw new VideoSourceException("The video source reported an unusable picture size.");
            duration = activate != 0 ? 0 : Duration();
            // Cameras adjust exposure and focus for about a second after they start; skip those frames.
            if (activate != 0)
                while (Environment.TickCount64 - opened < 1200 && ReadSample(out var warmup))
                    if (warmup != 0) Marshal.Release(warmup);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal static IReadOnlyList<CameraDevice> Cameras()
    {
        var found = new List<CameraDevice>();
        Enumerate((device, link, name) => { found.Add(new(link, name)); return false; });
        return found;
    }

    internal static MediaReader OpenCamera(string link)
    {
        nint chosen = 0;
        string? fallbackName = null;
        Enumerate((device, candidate, name) =>
        {
            if (!string.Equals(candidate, link, StringComparison.OrdinalIgnoreCase) && link.Length > 0) return false;
            Marshal.AddRef(device);
            chosen = device;
            fallbackName = name;
            return true;
        });
        if (chosen == 0)
            throw new VideoSourceException(link.Length == 0 ? "No camera found. Plug one in (or connect your phone as a webcam), then choose it under Find cameras."
                : "That camera isn't connected right now. Plug it back in, or choose another under Find cameras.");
        try
        {
            var iid = MediaSourceIid;
            Check(Call<ActivateObject>(chosen, 33)(chosen, ref iid, out var source));
            nint created;
            try
            {
                created = CreateReader(attributes => MFCreateSourceReaderFromMediaSource(source, attributes, out var r) is var hr && hr >= 0 ? r : Fail(hr));
            }
            finally { Marshal.Release(source); }
            return new(created, chosen);
        }
        catch
        {
            if (chosen != 0)
            {
                Call<NoArguments>(chosen, 34)(chosen);
                Marshal.Release(chosen);
            }
            throw;
        }
    }

    internal static MediaReader OpenAddress(string address)
    {
        var url = File.Exists(address) ? Path.GetFullPath(address) : address;
        return new(CreateReader(attributes => MFCreateSourceReaderFromURL(url, attributes, out var r) is var hr && hr >= 0 ? r : Fail(hr)), 0);
    }

    /// <summary>One frame, downscaled to at most <see cref="ScreenGlancer.MaximumEdge"/> px, BGRA top-down.</summary>
    internal (byte[] Pixels, int Width, int Height) Read()
    {
        if (reader == 0) throw new ObjectDisposedException(nameof(MediaReader));
        nint sample = 0;
        if (duration > 0)
        {
            Seek((Environment.TickCount64 - opened) * 10_000 % duration);
            for (var tries = 0; tries < 30 && sample == 0; tries++)
                if (!ReadSample(out sample)) { Seek(0); if (!ReadSample(out sample)) break; }
        }
        else
        {
            Check(Call<Flush>(reader, 10)(reader, FirstVideoStream));
            for (var tries = 0; tries < 30 && sample == 0; tries++)
                if (!ReadSample(out sample)) break;
        }
        if (sample == 0) throw new VideoSourceException("The video source ended or sent no picture.");
        try
        {
            Check(Call<ConvertToContiguousBuffer>(sample, 41)(sample, out var buffer));
            try
            {
                Check(Call<LockBuffer>(buffer, 3)(buffer, out var data, out _, out var length));
                try
                {
                    if (length < (long)Math.Abs(stride) * (height - 1) + width * 4)
                        throw new VideoSourceException("The video source sent an incomplete picture.");
                    var scale = Math.Min(1.0, (double)ScreenGlancer.MaximumEdge / Math.Max(width, height));
                    int outWidth = Math.Max(1, (int)Math.Round(width * scale)), outHeight = Math.Max(1, (int)Math.Round(height * scale));
                    var pixels = new byte[outWidth * outHeight * 4];
                    var rowStride = stride;
                    var top = rowStride >= 0 ? data : data + (nint)(-(long)rowStride * (height - 1));
                    DesktopDuplication.Downscale((row, column, into, count) =>
                        Marshal.Copy(top + (nint)((long)row * rowStride + column * 4L), into, 0, count * 4), 0, 0, width, height, pixels, outWidth, outHeight);
                    return (pixels, outWidth, outHeight);
                }
                finally { Call<NoArguments>(buffer, 4)(buffer); }
            }
            finally { Marshal.Release(buffer); }
        }
        finally { Marshal.Release(sample); }
    }

    public void Dispose()
    {
        if (reader != 0) Marshal.Release(reader);
        reader = 0;
        if (activate != 0)
        {
            Call<NoArguments>(activate, 34)(activate); // ShutdownObject: releases the camera (light off).
            Marshal.Release(activate);
        }
        activate = 0;
    }

    /// <summary>Plain-language advice for a Media Foundation failure.</summary>
    internal static string Explain(int code, bool camera) => unchecked((uint)code) switch
    {
        0x80070005 => "Windows blocked camera access. Open Settings > Privacy & security > Camera and turn on both Camera access and Let desktop apps access your camera, then start watching again.",
        0xC00D3704 or 0x800700AA or 0xC00D3EA3 or 0x80070020 => "The camera is busy: another app (a video call, the Camera app, OBS) is probably using it. Close that app or pick another camera.",
        0xC00D36C3 or 0xC00D36C4 or 0xC00D36C2 => "Windows can't open that kind of address. Use an http snapshot (.jpg) or MJPEG address from the camera app, or a webcam route (see the help text).",
        0xC00D36B4 or 0xC00D5212 => "Windows can't decode this video format. Use an MJPEG or snapshot (.jpg) address, or an H.264 video.",
        0x80070002 or 0x80070003 => camera ? "That camera isn't connected right now." : "Nothing was found at that address or file path.",
        0x80072EE7 or 0x80072EFD or 0x80072EE2 or 0xC00D2EE0 => "Couldn't reach that address. Check it, and that the camera and this PC are on the same network.",
        _ => (camera ? "The camera couldn't be read" : "The video source couldn't be read") + $" (Windows error 0x{code:X8}). Try unplugging and reconnecting it, or pick another source."
    };

    private void Seek(long position)
    {
        var value = Marshal.AllocHGlobal(PropVariantSize);
        try
        {
            Marshal.Copy(new byte[PropVariantSize], 0, value, PropVariantSize);
            Marshal.WriteInt16(value, 0, VtI8);
            Marshal.WriteInt64(value, 8, position);
            var format = Guid.Empty;
            Check(Call<SetCurrentPosition>(reader, 8)(reader, ref format, value));
        }
        finally { Marshal.FreeHGlobal(value); }
    }

    private long Duration()
    {
        var value = Marshal.AllocHGlobal(PropVariantSize);
        try
        {
            Marshal.Copy(new byte[PropVariantSize], 0, value, PropVariantSize);
            var key = PdDuration;
            if (Call<GetPresentationAttribute>(reader, 12)(reader, MediaSourceStream, ref key, value) < 0) return 0;
            return Marshal.ReadInt16(value) is VtUi8 or VtI8 ? Math.Max(0, Marshal.ReadInt64(value, 8)) : 0;
        }
        finally { Marshal.FreeHGlobal(value); }
    }

    // False at the end of the stream; a stream tick (gap) gives true with no sample.
    private bool ReadSample(out nint sample)
    {
        Check(Call<ReadSampleCall>(reader, 9)(reader, FirstVideoStream, 0, out _, out var flags, out _, out sample));
        if ((flags & ReaderError) != 0) throw new VideoSourceException("The video source stopped with an error. Reconnect it and start watching again.");
        return (flags & EndOfStream) == 0 || sample != 0;
    }

    private static nint CreateReader(Func<nint, nint> create)
    {
        Startup();
        Check(MFCreateAttributes(out var attributes, 2));
        try
        {
            var key = AdvancedVideoProcessing;
            Check(Call<SetUInt32>(attributes, 21)(attributes, ref key, 1));
            return create(attributes);
        }
        finally { Marshal.Release(attributes); }
    }

    private static void Enumerate(Func<nint, string, string, bool> visit)
    {
        Startup();
        Check(MFCreateAttributes(out var attributes, 1));
        nint array = 0;
        uint count = 0;
        try
        {
            var key = DevSourceType; var value = VideoCaptureSource;
            Check(Call<SetGuid>(attributes, 24)(attributes, ref key, ref value));
            Check(MFEnumDeviceSources(attributes, out array, out count));
            var stop = false;
            for (var i = 0; i < count; i++)
            {
                var device = Marshal.ReadIntPtr(array, i * IntPtr.Size);
                if (!stop)
                    stop = visit(device, String(device, DevSymbolicLink) ?? "", String(device, DevFriendlyName) ?? "Camera");
                Marshal.Release(device);
            }
        }
        finally
        {
            if (array != 0) Marshal.FreeCoTaskMem(array);
            Marshal.Release(attributes);
        }
    }

    private static string? String(nint attributes, Guid key)
    {
        if (Call<GetAllocatedString>(attributes, 13)(attributes, ref key, out var text, out _) < 0 || text == 0) return null;
        try { return Marshal.PtrToStringUni(text); }
        finally { Marshal.FreeCoTaskMem(text); }
    }

    private static void Startup()
    {
        lock (StartGate)
        {
            if (started) return;
            Check(MFStartup(MfVersion, 0));
            started = true;
        }
    }

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    private static nint Fail(int hr)
    {
        Marshal.ThrowExceptionForHR(hr);
        return 0;
    }

    private static T Call<T>(nint instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    private const uint MfVersion = 0x00020070, FirstVideoStream = 0xFFFFFFFC, AllStreams = 0xFFFFFFFE, MediaSourceStream = 0xFFFFFFFF;
    private const uint ReaderError = 0x1, EndOfStream = 0x2;
    private const short VtI8 = 20, VtUi8 = 21;
    private const int PropVariantSize = 24;
    private static readonly Guid DevSourceType = new("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");
    private static readonly Guid VideoCaptureSource = new("8ac3587a-4ae7-42d8-99e0-0a6013eef90f");
    private static readonly Guid DevFriendlyName = new("60d0e559-52f8-4fa2-bbce-acdb34a8ec01");
    private static readonly Guid DevSymbolicLink = new("58f0aad8-22bf-4f8a-bb3d-d2c4978c6e2f");
    private static readonly Guid AdvancedVideoProcessing = new("0f81da2c-b537-4672-a8b2-a681b17307a3");
    private static readonly Guid MtMajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MtSubtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MtFrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MtDefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid PdDuration = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
    private static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid VideoFormatRgb32 = new("00000016-0000-0010-8000-00aa00389b71");
    private static readonly Guid MediaSourceIid = new("279a808d-aec7-40c8-9c6b-a6b492c78a66");

    private delegate int NoArguments(nint self);
    private delegate int SetUInt32(nint self, ref Guid key, uint value);
    private delegate int GetUInt32(nint self, ref Guid key, out uint value);
    private delegate int GetUInt64(nint self, ref Guid key, out ulong value);
    private delegate int SetGuid(nint self, ref Guid key, ref Guid value);
    private delegate int GetAllocatedString(nint self, ref Guid key, out nint text, out uint length);
    private delegate int ActivateObject(nint self, ref Guid iid, out nint result);
    private delegate int SetStreamSelection(nint self, uint stream, int selected);
    private delegate int GetCurrentMediaType(nint self, uint stream, out nint type);
    private delegate int SetCurrentMediaType(nint self, uint stream, nint reserved, nint type);
    private delegate int SetCurrentPosition(nint self, ref Guid format, nint position);
    private delegate int ReadSampleCall(nint self, uint stream, uint control, out uint actual, out uint flags, out long timestamp, out nint sample);
    private delegate int Flush(nint self, uint stream);
    private delegate int GetPresentationAttribute(nint self, uint stream, ref Guid key, nint value);
    private delegate int ConvertToContiguousBuffer(nint self, out nint buffer);
    private delegate int LockBuffer(nint self, out nint data, out uint maximum, out uint current);

    [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] private static extern int MFCreateAttributes(out nint attributes, uint size);
    [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out nint type);
    [DllImport("mf.dll")] private static extern int MFEnumDeviceSources(nint attributes, out nint devices, out uint count);
    [DllImport("mfreadwrite.dll")] private static extern int MFCreateSourceReaderFromMediaSource(nint source, nint attributes, out nint reader);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] private static extern int MFCreateSourceReaderFromURL(string url, nint attributes, out nint reader);
}
