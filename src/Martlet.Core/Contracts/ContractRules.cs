using System.Text.RegularExpressions;

namespace Martlet.Core.Contracts;

public interface IContract
{
    void Validate();
}

public sealed class ContractException(ErrorCode code, string message) : Exception(message)
{
    public ErrorCode Code { get; } = code;
}

public static partial class ContractRules
{
    public const int MaxJsonBytes = 262_144;
    public const int MaxTextCharacters = 16_384;

    public static void Require(bool condition, string message, ErrorCode code = ErrorCode.InvalidContract)
    {
        if (!condition)
            throw new ContractException(code, message);
    }

    public static void Identifier(string? value)
    {
        Require(value is { Length: > 0 and <= 64 } && IdentifierPattern().IsMatch(value),
            "Use an identifier of 1-64 ASCII letters, digits, dots, underscores or hyphens.");
    }

    public static void Text(string? value, int maximum)
    {
        Require(value is not null && value.Length <= maximum && !value.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'),
            "Text is missing, too long or contains unsupported control characters.");
    }

    public static void Defined<T>(T value) where T : struct, Enum =>
        Require(Enum.IsDefined(value), "An enum value is unsupported.");

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}

public sealed record ContractVersion : IContract
{
    public required int Major { get; init; }
    public required int Minor { get; init; }
    public static ContractVersion Current => new() { Major = 1, Minor = 0 };

    public void Validate()
    {
        ContractRules.Require(Major == 1, "Use a compatible contract major version.", ErrorCode.UnsupportedVersion);
        ContractRules.Require(Minor is >= 0 and <= 9999, "Contract minor version is out of range.");
    }
}

public enum EvidenceProvenance { Unknown, NotRun, Fixture, Live }
public enum Stage { Application, Settings, Transcription, TurnPolicy, Generation, Synthesis, Playback, Provider, Capture }
public enum ErrorCode
{
    InvalidContract, UnsupportedVersion, PayloadTooLarge,
    SettingsMalformed, SettingsInaccessible, SettingsConflict,
    NotConfigured, NotImplemented, ProviderCapability, ProviderFailed, StreamTruncated,
    AudioDeviceUnavailable, AudioDeviceLost, AudioFormatUnsupported, AudioPlaybackFailed, DeadlineExceeded,
    AudioCaptureFailed, AudioAccessDenied, AudioDeviceBusy, AudioDeviceChanged
}

public sealed record MartletError : IContract
{
    public required ErrorCode Code { get; init; }
    public required Stage Stage { get; init; }
    public required bool Retryable { get; init; }
    public required string Summary { get; init; }
    public required string ActionId { get; init; }
    public Guid? TraceId { get; init; }

    public void Validate()
    {
        ContractRules.Defined(Code);
        ContractRules.Defined(Stage);
        ContractRules.Text(Summary, 512);
        ContractRules.Require(!string.IsNullOrWhiteSpace(Summary), "An error needs a user-safe summary.");
        ContractRules.Identifier(ActionId);
        ContractRules.Require(TraceId != Guid.Empty, "A trace identifier cannot be empty.");
    }
}
