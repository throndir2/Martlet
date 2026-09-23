using System.Security.Cryptography;
using System.Text;

namespace Martlet.F5;

internal static class F5ReferenceDigests
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string TranscriptRevision(string transcript)
    {
        F5Guard.Utf8Text(transcript, F5ReferenceLimits.MaximumTranscriptCharacters,
            F5ReferenceLimits.MaximumTranscriptUtf8Bytes);
        var bytes = StrictUtf8.GetBytes(transcript);
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static string ReferenceRevision(string audioSha256, string transcriptRevision)
    {
        F5Guard.Sha256(audioSha256);
        F5Guard.Sha256(transcriptRevision);
        Span<byte> material = stackalloc byte[64];
        Convert.FromHexString(audioSha256).CopyTo(material);
        Convert.FromHexString(transcriptRevision).CopyTo(material[32..]);
        return Convert.ToHexStringLower(SHA256.HashData(material));
    }

    internal static void Validate(F5ReferenceSnapshotDocument snapshot)
    {
        var transcriptRevision = TranscriptRevision(snapshot.Transcript);
        F5Guard.Require(F5Guard.FixedTimeEquals(
            transcriptRevision, snapshot.TranscriptRevision), F5Failure.CorruptStore);
        var referenceRevision = ReferenceRevision(
            snapshot.AudioSha256, snapshot.TranscriptRevision);
        F5Guard.Require(F5Guard.FixedTimeEquals(
            referenceRevision, snapshot.ReferenceRevision), F5Failure.CorruptStore);
    }
}
