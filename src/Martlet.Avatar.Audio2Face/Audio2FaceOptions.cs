using System.Net;

namespace Martlet.Avatar.Audio2Face;

public sealed record Audio2FaceOptions
{
    public required Uri Endpoint { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public int MaxOutputFrames { get; init; } = 10_800;
    public int MaxResponseBytes { get; init; } = 64 * 1024 * 1024;

    public void Validate()
    {
        if (Endpoint is null || !Endpoint.IsAbsoluteUri ||
            Endpoint.Scheme != Uri.UriSchemeHttp ||
            !IPAddress.TryParse(Endpoint.Host.Trim('[', ']'), out var address) ||
            !IPAddress.IsLoopback(address) || Endpoint.Port is < 1 or > 65535 ||
            Endpoint.AbsolutePath != "/" || Endpoint.Query.Length != 0 ||
            Endpoint.Fragment.Length != 0 || Endpoint.UserInfo.Length != 0)
            throw new ArgumentException("Audio2Face requires an explicit numeric HTTP loopback endpoint with no path or credentials.");
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromSeconds(90) ||
            IdleTimeout <= TimeSpan.Zero || IdleTimeout > RequestTimeout ||
            MaxOutputFrames is < 1 or > 10_800 ||
            MaxResponseBytes is < 1024 or > 64 * 1024 * 1024)
            throw new ArgumentException("Audio2Face limits exceed the supported bounds.");
    }

    public override string ToString() => nameof(Audio2FaceOptions);
}

public enum Audio2FaceFailure
{
    AuthorizationRequired, AuthorizationExpired, AuthorizationConsumed, InvalidBinding,
    InvalidProtocol, InvalidInput, Backpressure, LimitExceeded, UpstreamFailure, TransportFailure, DeadlineExceeded
}

public sealed class Audio2FaceException(Audio2FaceFailure failure)
    : Exception($"Audio2Face operation failed: {failure}.")
{
    public Audio2FaceFailure Failure { get; } = failure;
}

public sealed record Audio2FacePrerequisiteReport(
    bool ConfigurationValid,
    bool RuntimeVerified,
    IReadOnlyList<string> Requirements);
