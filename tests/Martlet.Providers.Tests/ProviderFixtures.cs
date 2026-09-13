using System.Buffers.Binary;
using System.Net;
using System.Text;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;

namespace Martlet.Providers.Tests;

// Authored synthetic bytes and responses, never recordings or live provider evidence.
internal static class ProviderFixtures
{
    public const string Secret = "fixture-secret-DO-NOT-LOG";
    public const string ContentCanary = "private-audio-prompt-transcript-DO-NOT-LOG";
    public static DateTimeOffset Now => new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
    public static PcmFormat Format(int rate = 16000) => new()
    {
        SampleRate = rate, Channels = 1, Encoding = PcmEncoding.Signed16LittleEndian
    };
    public static ProviderRequestContext Context() => new()
    {
        Ids = new()
        {
            SessionId = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111"),
            TurnId = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222"),
            RequestId = Guid.Parse("cccccccc-3333-3333-3333-333333333333")
        },
        Epoch = 7, Deadline = Now.AddSeconds(30)
    };
    public static ProviderCredentialBinding Binding(string model = "gpt-transcribe") =>
        new(OpenAiTranscriptionCatalog.Origin, ProviderRole.Stt, model);
    public static AudioUploadAuthorization Authorize(
        ProviderRequestContext context, TranscriptionLimits limits,
        ProviderCredentialBinding? binding = null, DateTimeOffset? expiry = null,
        bool audio = true, bool charges = true) =>
        new(binding ?? Binding(), context.Ids, context.Epoch, limits, expiry ?? Now.AddMinutes(1), audio, charges);

    public static byte[] Wave(int samples = 3200, int rate = 16000)
    {
        byte[] bytes = new byte[44 + samples * 2];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)bytes.Length - 8);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(bytes, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), (uint)rate);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), (uint)rate * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(bytes, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), (uint)samples * 2);
        for (int offset = 44; offset < bytes.Length; offset++)
            bytes[offset] = (byte)(offset % 251);
        return bytes;
    }

    public static HttpResponseMessage Json(string json = """{"text":"Synthetic fixture transcript."}""", int status = 200) =>
        new((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

internal sealed class FixtureCredentials : IProviderCredentialSource
{
    public int Calls { get; private set; }
    public Func<ProviderCredentialBinding, CancellationToken, ValueTask<BoundProviderCredential?>> Resolve { get; set; } =
        (binding, _) => ValueTask.FromResult<BoundProviderCredential?>(new(binding, ProviderFixtures.Secret));

    public ValueTask<BoundProviderCredential?> ResolveAsync(ProviderCredentialBinding binding, CancellationToken cancellationToken)
    {
        Calls++;
        return Resolve(binding, cancellationToken);
    }
}

internal sealed class RecordingHandler : HttpMessageHandler
{
    public int Calls { get; private set; }
    public bool Disposed { get; private set; }
    public byte[] Body { get; private set; } = [];
    public Uri? Uri { get; private set; }
    public string? Authorization { get; private set; }
    public string? ContentType { get; private set; }
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
        (_, _) => Task.FromResult(ProviderFixtures.Json());

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        Uri = request.RequestUri;
        Authorization = request.Headers.Authorization?.ToString();
        ContentType = request.Content!.Headers.ContentType?.ToString();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(HttpVersion.Version11, request.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionExact, request.VersionPolicy);
        Assert.Equal("application/json", Assert.Single(request.Headers.Accept).MediaType);
        using var stream = new MemoryStream();
        await request.Content.CopyToAsync(stream, cancellationToken);
        Body = stream.ToArray();
        return await Respond(request, cancellationToken);
    }
    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}

internal sealed class FixtureClock : TimeProvider
{
    private long ticks;
    private readonly List<FixtureTimer> timers = [];
    public override DateTimeOffset GetUtcNow() => ProviderFixtures.Now.AddTicks(ticks);
    public override long GetTimestamp() => ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FixtureTimer(this, callback, state);
        timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }
    public void Advance(TimeSpan duration)
    {
        ticks += duration.Ticks;
        foreach (var timer in timers.ToArray())
            timer.Fire();
    }
    private sealed class FixtureTimer(FixtureClock clock, TimerCallback callback, object? state) : ITimer
    {
        private long? due;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.ticks + dueTime.Ticks;
            return true;
        }
        public void Fire()
        {
            if (due is { } value && clock.ticks >= value)
            {
                due = null;
                callback(state);
            }
        }
        public void Dispose() => due = null;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

internal sealed class NonSeekableBody(byte[] bytes, Action? onRead = null) : Stream
{
    private int position;
    public int BytesRead { get; private set; }
    public bool Disposed { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        onRead?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        int count = Math.Min(buffer.Length, bytes.Length - position);
        bytes.AsMemory(position, count).CopyTo(buffer);
        position += count;
        BytesRead += count;
        return ValueTask.FromResult(count);
    }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
