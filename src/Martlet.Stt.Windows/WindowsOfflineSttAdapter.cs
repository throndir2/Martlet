using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Martlet.LocalStt;

namespace Martlet.Stt.Windows;

public sealed class WindowsOfflineSttAdapter : IAsyncDisposable
{
    public static TimeSpan MaximumActionLifetime => TimeSpan.FromSeconds(30);
    private readonly IOfflineRecognizerFactory factory;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim slot = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private int disposed;
    private int quarantined;

    public WindowsOfflineSttAdapter() : this(new SystemSpeechRecognizerFactory(), TimeProvider.System) { }

    internal WindowsOfflineSttAdapter(IOfflineRecognizerFactory factory, TimeProvider clock)
    {
        this.factory = factory;
        this.clock = clock;
    }

    public async Task<WindowsOfflineRecognizerDiscovery> GetInstalledRecognizersAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        if (cancellationToken.IsCancellationRequested)
            return new([], Canceled: true);
        if (!await slot.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
            return new([], WindowsOfflineSttFailure.Create(WindowsOfflineSttFailureCode.Busy));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        try
        {
            if (quarantined != 0)
                return new([], WindowsOfflineSttFailure.Create(WindowsOfflineSttFailureCode.Quarantined));
            var recognizers = await Task.Run(() =>
            {
                lifetime.Token.ThrowIfCancellationRequested();
                return factory.Discover();
            }, CancellationToken.None).ConfigureAwait(false);
            if (lifetime.IsCancellationRequested)
                return new([], Canceled: true);
            if (recognizers.IsDefaultOrEmpty)
                return new([], WindowsOfflineSttFailure.Create(WindowsOfflineSttFailureCode.RecognizerUnavailable));
            if (recognizers.Length > 128 || recognizers.Any(item =>
                    !ValidIdentity(item.Id) || !ValidIdentity(item.Culture) || !ValidIdentity(item.Name)) ||
                recognizers.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != recognizers.Length)
                return new([], WindowsOfflineSttFailure.Create(WindowsOfflineSttFailureCode.RecognitionFailed));
            return new(recognizers);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return new([], Canceled: true);
        }
        catch (Exception error) when (IsRecognizerError(error))
        {
            return new([], WindowsOfflineSttFailure.Create(MapError(error)));
        }
        finally
        {
            slot.Release();
        }
    }

    public async Task<WindowsOfflineSttResult> TranscribeAsync(
        WindowsOfflineSttRequest request, CanonicalWaveAudio audio,
        WindowsOfflineSttAuthorization? authorization, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(audio);
        if (cancellationToken.IsCancellationRequested)
            return Canceled();
        var started = clock.GetTimestamp();
        var now = clock.GetUtcNow();
        var remaining = request.Deadline - now;
        if (request.OperationId == Guid.Empty || !ValidIdentity(request.RecognizerId))
            return Failed(WindowsOfflineSttFailureCode.InvalidRequest);
        if (remaining <= TimeSpan.Zero || remaining > MaximumActionLifetime)
            return Failed(WindowsOfflineSttFailureCode.DeadlineExceeded);
        if (authorization is null || !authorization.AllowLocalRecognition)
            return Failed(WindowsOfflineSttFailureCode.AuthorizationMissing);
        if (authorization.OperationId != request.OperationId || authorization.Deadline != request.Deadline ||
            authorization.RecognizerId != request.RecognizerId ||
            authorization.AudioSha256 != audio.Sha256 || authorization.AudioBytes != audio.ByteLength)
            return Failed(WindowsOfflineSttFailureCode.AuthorizationMismatch);
        if (!await slot.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
            return Failed(WindowsOfflineSttFailureCode.Busy);
        using var deadline = new CancellationTokenSource(remaining, clock);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, shutdown.Token, deadline.Token);
        byte[]? wave = null;
        try
        {
            if (quarantined != 0)
                return Failed(WindowsOfflineSttFailureCode.Quarantined);
            if (Cutoff() is { } early)
                return early;
            if (!authorization.TryConsume())
                return Failed(WindowsOfflineSttFailureCode.AuthorizationConsumed);
            wave = audio.CopyWave();
            var result = await Task.Run(() => RunOwned(wave), CancellationToken.None).ConfigureAwait(false);
            if (result.Failure?.Code == WindowsOfflineSttFailureCode.CleanupFailed)
                return result;
            return Cutoff() ?? result;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return Cutoff() ?? Canceled();
        }
        catch (Exception error) when (IsRecognizerError(error))
        {
            return Cutoff() ?? Failed(MapError(error));
        }
        finally
        {
            if (wave is not null)
                CryptographicOperations.ZeroMemory(wave);
            slot.Release();
        }

        WindowsOfflineSttResult RunOwned(byte[] bytes)
        {
            if (Cutoff() is { } cutoff)
                return cutoff;
            IOfflineRecognizerSession? session = null;
            var result = Failed(WindowsOfflineSttFailureCode.RecognitionFailed);
            try
            {
                session = factory.Create(request.RecognizerId);
                if (Cutoff() is { } beforeInput)
                    result = beforeInput;
                else
                {
                    using var stream = new MemoryStream(bytes, writable: false);
                    var text = session.Recognize(stream, lifetime.Token);
                    result = ValidateTranscript(request.OperationId, text, factory.Provenance);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                result = Cutoff() ?? Canceled();
            }
            catch (Exception error) when (IsRecognizerError(error))
            {
                result = Failed(MapError(error));
            }
            finally
            {
                if (session is not null)
                {
                    try { session.Dispose(); }
                    catch (Exception error) when (IsRecognizerError(error))
                    {
                        Interlocked.Exchange(ref quarantined, 1);
                        result = Failed(WindowsOfflineSttFailureCode.CleanupFailed);
                    }
                }
            }
            return result;
        }

        WindowsOfflineSttResult? Cutoff() =>
            cancellationToken.IsCancellationRequested || shutdown.IsCancellationRequested ? Canceled() :
            deadline.IsCancellationRequested || clock.GetUtcNow() >= request.Deadline ||
            clock.GetElapsedTime(started) >= remaining ? Failed(WindowsOfflineSttFailureCode.DeadlineExceeded) : null;
        WindowsOfflineSttResult Canceled() => new(request.OperationId, WindowsOfflineSttOutcome.Canceled);
        WindowsOfflineSttResult Failed(WindowsOfflineSttFailureCode code) =>
            new(request.OperationId, WindowsOfflineSttOutcome.Failed, failure: code);
    }

    internal static WindowsOfflineSttResult ValidateTranscript(Guid operationId, string text,
        WindowsOfflineSttProvenance provenance)
    {
        if (text.Length > LocalSttPackageManifest.MaximumTranscriptCharacters ||
            Encoding.UTF8.GetByteCount(text) > LocalSttPackageManifest.MaximumTranscriptBytes)
            return new(operationId, WindowsOfflineSttOutcome.Failed, failure: WindowsOfflineSttFailureCode.TranscriptLimit);
        if (text.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t') ||
            !WellFormedUnicode(text))
            return new(operationId, WindowsOfflineSttOutcome.Failed, failure: WindowsOfflineSttFailureCode.TranscriptInvalid);
        text = text.Trim();
        return text.Length == 0 ? new(operationId, WindowsOfflineSttOutcome.NoSpeech, provenance: provenance) :
            new(operationId, WindowsOfflineSttOutcome.Completed, text, provenance: provenance);
    }

    internal static bool ValidIdentity(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 4096 &&
        !value.Any(char.IsControl) && WellFormedUnicode(value) && Encoding.UTF8.GetByteCount(value) <= 16_384;

    private static bool WellFormedUnicode(string value)
    {
        for (var i = 0; i < value.Length; i++)
            if (char.IsSurrogate(value[i]) &&
                (!char.IsHighSurrogate(value[i]) || ++i >= value.Length || !char.IsLowSurrogate(value[i])))
                return false;
        return true;
    }

    internal static bool IsRecognizerError(Exception error) => error is
        PlatformNotSupportedException or InvalidOperationException or ArgumentException or
        IOException or UnauthorizedAccessException or COMException or NotSupportedException;

    private static WindowsOfflineSttFailureCode MapError(Exception error) => error switch
    {
        PlatformNotSupportedException => WindowsOfflineSttFailureCode.UnsupportedHost,
        RecognizerUnavailableException => WindowsOfflineSttFailureCode.RecognizerUnavailable,
        TranscriptLimitException => WindowsOfflineSttFailureCode.TranscriptLimit,
        _ => WindowsOfflineSttFailureCode.RecognitionFailed
    };

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref disposed, 1);
        await shutdown.CancelAsync().ConfigureAwait(false);
        await slot.WaitAsync().ConfigureAwait(false);
        slot.Release();
    }
}

internal interface IOfflineRecognizerFactory
{
    WindowsOfflineSttProvenance Provenance { get; }
    ImmutableArray<WindowsOfflineRecognizer> Discover();
    IOfflineRecognizerSession Create(string recognizerId);
}

internal interface IOfflineRecognizerSession : IDisposable
{
    string Recognize(Stream wave, CancellationToken cancellationToken);
}

internal sealed class RecognizerUnavailableException : InvalidOperationException;
internal sealed class TranscriptLimitException : InvalidOperationException;
