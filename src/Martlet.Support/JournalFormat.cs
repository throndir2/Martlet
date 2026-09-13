using System.Text.Json.Serialization;
using System.Security.Cryptography;
using Martlet.Core.Contracts;

namespace Martlet.Support;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record JournalOptions : IContract
{
    public long MaximumTotalBytes { get; init; } = 50 * 1024 * 1024;
    public int MaximumSegmentBytes { get; init; } = 1024 * 1024;
    public int MaximumRecordBytes { get; init; } = 8192;
    public int RetentionDays { get; init; } = 7;

    public void Validate()
    {
        Guard.Require(MaximumTotalBytes is >= 65_536 and <= 50 * 1024 * 1024);
        Guard.Require(MaximumRecordBytes is >= 2048 and <= 8192);
        Guard.Require(MaximumSegmentBytes >= MaximumRecordBytes && MaximumSegmentBytes <= MaximumTotalBytes - 32_768);
        Guard.Require(RetentionDays is >= 1 and <= 7);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SegmentSlot : IContract
{
    public required int Slot { get; init; }
    public required long FirstSequence { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
    public void Validate()
    {
        Guard.Require(Slot is >= 0 and < 32 && FirstSequence >= 1);
        Guard.Utc(CreatedUtc);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record JournalManifest : IContract
{
    public required int SchemaVersion { get; init; }
    public required Guid Owner { get; init; }
    public required JournalOptions Options { get; init; }
    public required long NextSequence { get; init; }
    public required SegmentSlot[] Segments { get; init; }
    public required string Sha256 { get; init; }
    private string Digest() => Convert.ToHexStringLower(SHA256.HashData(
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion, Owner, Options, NextSequence, Segments })));
    internal JournalManifest Seal() => this with { Sha256 = Digest() };
    public void Validate()
    {
        Guard.Require(SchemaVersion == 1, SupportFailure.UnsupportedVersion);
        Guard.Require(Owner != Guid.Empty && NextSequence >= 1 && Options is not null &&
            Segments is { Length: <= 32 });
        Options!.Validate();
        foreach (var segment in Segments!) { Guard.Require(segment is not null); segment!.Validate(); }
        Guard.Require(Segments.Select(s => s.Slot).Distinct().Count() == Segments.Length &&
            Segments.Select(s => s.FirstSequence).Distinct().Count() == Segments.Length &&
            Segments.All(s => s.FirstSequence < NextSequence) &&
            Segments.Select(s => s.FirstSequence).SequenceEqual(Segments.Select(s => s.FirstSequence).Order()));
        Guard.Require(Sha256 == Digest(), SupportFailure.CorruptJournal);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record JournalRecord : IContract
{
    public required int SchemaVersion { get; init; }
    public required Guid JournalId { get; init; }
    public required long Sequence { get; init; }
    public required DateTimeOffset AcceptedUtc { get; init; }
    public required DiagnosticEvent Event { get; init; }
    public void Validate()
    {
        Guard.Require(SchemaVersion == 1, SupportFailure.UnsupportedVersion);
        Guard.Require(JournalId != Guid.Empty && Sequence >= 1 && Event is not null);
        Guard.Utc(AcceptedUtc);
        Event!.Validate();
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record JournalFrame : IContract
{
    public required int SchemaVersion { get; init; }
    public required JournalRecord Record { get; init; }
    public required string Sha256 { get; init; }
    internal static JournalFrame Create(JournalRecord record) => new()
    {
        SchemaVersion = 1, Record = record, Sha256 = Digest(record)
    };
    private static string Digest(JournalRecord record) =>
        Convert.ToHexStringLower(SHA256.HashData(Storage.Compact(record, 8192)));
    public void Validate()
    {
        Guard.Require(SchemaVersion == 1, SupportFailure.UnsupportedVersion);
        Guard.Require(Record is not null);
        Record!.Validate();
        Guard.Require(Sha256 == Digest(Record), SupportFailure.CorruptJournal);
    }
}

public sealed record JournalRecovery(int TruncatedTailBytes, int AbandonedReservations);
public sealed record AppendReceipt(long Sequence, int EncodedBytes);

public sealed record LogRange(DateTimeOffset FromUtc, DateTimeOffset ThroughUtc, int MaximumRecords = 2048,
    int MaximumBytes = 2 * 1024 * 1024)
{
    internal void Validate()
    {
        Guard.Utc(FromUtc); Guard.Utc(ThroughUtc);
        Guard.Require(FromUtc <= ThroughUtc && MaximumRecords is >= 1 and <= 2048 &&
            MaximumBytes is >= 8192 and <= 2 * 1024 * 1024);
    }
}

public sealed class JournalSelection
{
    public LogRange Range { get; }
    public IReadOnlyList<JournalRecord> Records { get; }
    public static JournalSelection Empty(LogRange range)
    {
        Guard.Require(range is not null);
        range!.Validate();
        return new(range, []);
    }

    internal JournalSelection(LogRange range, JournalRecord[] records)
    {
        Range = range;
        Records = Array.AsReadOnly(records);
    }
}
