using System.Text;

namespace Martlet.LocalStt;

internal static class TranscriptParser
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static bool DiagnosticsAreSafe(
        ReadOnlyMemory<byte> standardOutput,
        ReadOnlyMemory<byte> standardError)
    {
        if (standardOutput.Length > LocalSttPackageManifest.MaximumStandardOutputBytes ||
            standardError.Length > LocalSttPackageManifest.MaximumStandardErrorBytes)
            return false;
        try
        {
            var stdout = StrictUtf8.GetString(standardOutput.Span);
            _ = StrictUtf8.GetCharCount(standardError.Span);
            return string.IsNullOrWhiteSpace(stdout);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    internal static LocalSttResult Parse(
        Guid operationId,
        ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > LocalSttPackageManifest.MaximumTranscriptBytes)
            return LocalSttResult.Failed(operationId, LocalSttFailureCode.TranscriptLimit);
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes.Span);
        }
        catch (DecoderFallbackException)
        {
            return LocalSttResult.Failed(operationId, LocalSttFailureCode.TranscriptMalformed);
        }
        if (text.Length != 0 && text[0] == '\uFEFF')
            return LocalSttResult.Failed(operationId, LocalSttFailureCode.TranscriptMalformed);
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        if (text.Length == 0)
            return new(operationId, LocalSttOutcome.NoSpeech);
        if (text.Length > LocalSttPackageManifest.MaximumTranscriptCharacters ||
            text.Any(character => char.IsControl(character) &&
                character is not '\n' and not '\t'))
            return LocalSttResult.Failed(operationId, text.Length >
                LocalSttPackageManifest.MaximumTranscriptCharacters
                ? LocalSttFailureCode.TranscriptLimit
                : LocalSttFailureCode.TranscriptMalformed);
        return new(operationId, LocalSttOutcome.Completed, text: text);
    }
}
