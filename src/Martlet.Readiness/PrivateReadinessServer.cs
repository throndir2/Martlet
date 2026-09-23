using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Martlet.Readiness;

internal enum PrivateReadinessFailure
{
    None,
    TimedOut,
    ProcessExited,
    WrongNonce,
    WrongProtocol,
    WrongPurpose,
    WrongVersion,
    WrongProfile,
    WrongSettingsRevision,
    WrongPayload,
    WrongExecutable,
    WrongProcess,
    WrongProcessPath,
    Degraded,
    Failed,
    Malformed,
    Unavailable,
    Cancelled
}

internal sealed record PrivateReadinessResult(
    PrivateReadinessFailure Failure,
    ReadinessState? State = null,
    int? ExitCode = null)
{
    internal bool IsInitialized =>
        Failure == PrivateReadinessFailure.None && State == ReadinessState.Initialized;
}

internal sealed record PrivateReadinessBinding(
    DesktopReadinessPurpose Purpose,
    string Version,
    Guid ProfileId,
    string SettingsRevision,
    string PayloadSha256,
    string ExecutableSha256,
    string ExecutablePath);

internal sealed class PrivateReadinessServer : IAsyncDisposable
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private readonly PrivateReadinessBinding binding;
    private readonly TimeSpan timeout;
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly NamedPipeServerStream pipe;
    private readonly string nonce;
    private int waited;

    internal string PipeName { get; }
    internal DateTimeOffset DeadlineUtc { get; }
    internal bool IsExpired => elapsed.Elapsed >= timeout || DateTimeOffset.UtcNow >= DeadlineUtc;

    internal PrivateReadinessServer(PrivateReadinessBinding binding, TimeSpan timeout,
        DateTimeOffset? deadlineUtc = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!OperatingSystem.IsWindows() || timeout <= TimeSpan.Zero ||
            timeout > TimeSpan.FromSeconds(30) ||
            !ReadinessProtocol.ValidVersion(binding.Version) ||
            binding.ProfileId == Guid.Empty ||
            !ReadinessProtocol.ValidSettingsRevision(binding.SettingsRevision) ||
            !ReadinessProtocol.IsLowerHex(binding.PayloadSha256, 64) ||
            !ReadinessProtocol.IsLowerHex(binding.ExecutableSha256, 64) ||
            !Path.IsPathFullyQualified(binding.ExecutablePath) ||
            deadlineUtc is { } supplied &&
            (supplied <= DateTimeOffset.UtcNow ||
             supplied - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(30)))
            throw new ArgumentException("Supply a supported bounded readiness binding.");
        this.binding = binding;
        this.timeout = timeout;
        nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        PipeName = "martlet-rdy-" + Guid.NewGuid().ToString("N") + "-" +
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        DeadlineUtc = deadlineUtc ?? DateTimeOffset.UtcNow + timeout;
        pipe = new(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            inBufferSize: ReadinessProtocol.MaximumMessageBytes + sizeof(int),
            outBufferSize: 1);
    }

    internal IReadOnlyDictionary<string, string> Environment =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ReadinessEnvironment.PipeName] = PipeName,
            [ReadinessEnvironment.Nonce] = nonce,
            [ReadinessEnvironment.ProtocolVersion] =
                ReadinessProtocol.ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [ReadinessEnvironment.Purpose] = binding.Purpose.ToString(),
            [ReadinessEnvironment.Version] = binding.Version,
            [ReadinessEnvironment.ProfileId] = binding.ProfileId.ToString("D"),
            [ReadinessEnvironment.SettingsRevision] = binding.SettingsRevision,
            [ReadinessEnvironment.PayloadSha256] = binding.PayloadSha256,
            [ReadinessEnvironment.ExecutableSha256] = binding.ExecutableSha256,
            [ReadinessEnvironment.DeadlineUtc] = DeadlineUtc.ToString("O",
                System.Globalization.CultureInfo.InvariantCulture)
        };

    internal async Task<PrivateReadinessResult> WaitAsync(
        int expectedProcessId,
        CancellationToken processExited,
        Func<int?> readExitCode,
        CancellationToken token)
    {
        if (Interlocked.Exchange(ref waited, 1) != 0)
            return new(PrivateReadinessFailure.Unavailable);
        if (expectedProcessId <= 0)
            return new(PrivateReadinessFailure.WrongProcess);
        try
        {
            var path = ProcessPath(expectedProcessId);
            if (!SamePath(path, binding.ExecutablePath))
                return new(PrivateReadinessFailure.WrongProcessPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return processExited.IsCancellationRequested
                ? new(PrivateReadinessFailure.ProcessExited, ExitCode: readExitCode())
                : new(PrivateReadinessFailure.Unavailable);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            token, processExited);
        var remaining = timeout - elapsed.Elapsed;
        if (remaining <= TimeSpan.Zero)
            return new(PrivateReadinessFailure.TimedOut);
        deadline.CancelAfter(remaining);
        try
        {
            await pipe.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientProcessId))
                return new(PrivateReadinessFailure.Unavailable);
            if (clientProcessId != (uint)expectedProcessId)
                return new(PrivateReadinessFailure.WrongProcess);

            var lengthBytes = new byte[sizeof(int)];
            await ReadExactAsync(lengthBytes, deadline.Token).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
            if (length is <= 0 or > ReadinessProtocol.MaximumMessageBytes)
                return new(PrivateReadinessFailure.Malformed);
            var bytes = new byte[length];
            await ReadExactAsync(bytes, deadline.Token).ConfigureAwait(false);
            if (IsExpired)
                return new(PrivateReadinessFailure.TimedOut);
            var message = Parse(bytes);
            if (message is null)
                return new(PrivateReadinessFailure.Malformed);
            if (processExited.IsCancellationRequested)
                return new(PrivateReadinessFailure.ProcessExited, ExitCode: readExitCode());
            if (!SamePath(ProcessPath(expectedProcessId), binding.ExecutablePath))
                return new(PrivateReadinessFailure.WrongProcessPath);
            var failure = Validate(message, expectedProcessId);
            return failure == PrivateReadinessFailure.None
                ? new(failure, message.State)
                : new(failure, message.State);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new(PrivateReadinessFailure.Cancelled);
        }
        catch (OperationCanceledException) when (processExited.IsCancellationRequested)
        {
            return new(PrivateReadinessFailure.ProcessExited, ExitCode: readExitCode());
        }
        catch (OperationCanceledException)
        {
            return new(PrivateReadinessFailure.TimedOut);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(PrivateReadinessFailure.Unavailable);
        }
    }

    private async Task ReadExactAsync(byte[] buffer, CancellationToken token)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await pipe.ReadAsync(buffer.AsMemory(read), token).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException();
            read += count;
        }
    }

    private ReadinessMessage? Parse(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes,
                new JsonDocumentOptions { MaxDepth = ReadinessProtocol.Json.MaxDepth });
            CheckDuplicates(document.RootElement);
            var message = JsonSerializer.Deserialize<ReadinessMessage>(
                bytes, ReadinessProtocol.Json);
            return message is not null &&
                ReadinessProtocol.Serialize(message).AsSpan().SequenceEqual(bytes)
                    ? message
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private PrivateReadinessFailure Validate(ReadinessMessage message, int expectedProcessId)
    {
        if (message.FormatVersion != ReadinessProtocol.FormatVersion ||
            message.ProtocolVersion != ReadinessProtocol.ProtocolVersion)
            return PrivateReadinessFailure.WrongProtocol;
        if (!ReadinessProtocol.IsLowerHex(message.Nonce, 64) ||
            !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(message.Nonce),
                Convert.FromHexString(nonce)))
            return PrivateReadinessFailure.WrongNonce;
        if (message.Purpose != binding.Purpose)
            return PrivateReadinessFailure.WrongPurpose;
        if (message.Version != binding.Version)
            return PrivateReadinessFailure.WrongVersion;
        if (message.ProfileId != binding.ProfileId)
            return PrivateReadinessFailure.WrongProfile;
        if (message.SettingsRevision != binding.SettingsRevision)
            return PrivateReadinessFailure.WrongSettingsRevision;
        if (message.PayloadSha256 != binding.PayloadSha256)
            return PrivateReadinessFailure.WrongPayload;
        if (message.ExecutableSha256 != binding.ExecutableSha256)
            return PrivateReadinessFailure.WrongExecutable;
        if (message.ProcessId != expectedProcessId)
            return PrivateReadinessFailure.WrongProcess;
        if (message.DeadlineUtc != DeadlineUtc)
            return PrivateReadinessFailure.TimedOut;
        return message.State switch
        {
            ReadinessState.Initialized => PrivateReadinessFailure.None,
            ReadinessState.Degraded => PrivateReadinessFailure.Degraded,
            ReadinessState.Failed => PrivateReadinessFailure.Failed,
            _ => PrivateReadinessFailure.Malformed
        };
    }

    private static void CheckDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException();
                CheckDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) CheckDuplicates(item);
        }
    }

    private static string ProcessPath(int processId)
    {
        using var process = OpenProcess(
            ProcessQueryLimitedInformation, inheritHandle: false, (uint)processId);
        if (process.IsInvalid)
            throw new IOException();
        var capacity = 32768;
        var path = new StringBuilder(capacity);
        if (!QueryFullProcessImageNameW(process, 0, path, ref capacity) || capacity == 0)
            throw new IOException();
        return Path.GetFullPath(path.ToString());
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    public ValueTask DisposeAsync()
    {
        pipe.Dispose();
        return ValueTask.CompletedTask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(
        SafeProcessHandle process,
        uint flags,
        StringBuilder executableName,
        ref int size);
}
