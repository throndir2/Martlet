using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using Martlet.Core.Speakers;

namespace Martlet.Desktop;

/// <summary>One short recording of a voice, newest first in <see cref="VoiceClips.List"/>.</summary>
internal sealed record VoiceClip(string Path, DateTimeOffset At, double Seconds);

/// <summary>The last few things a voice said, kept only until the owner says who it is, so People can play them to help tell.
/// Each voice keeps at most <see cref="MaximumClips"/> clips of at most <see cref="MaximumSeconds"/> seconds in
/// voice-clips\&lt;voice ID&gt;\ in Martlet's data folder, on this PC only (never synced or sent). Naming the voice, marking it
/// as the owner's, forgetting it or turning clips off deletes them. Whether clips are kept (voice-clips.txt) is this PC's
/// choice, on unless the owner turned it off.</summary>
internal sealed class VoiceClips
{
    internal const string Folder = "voice-clips";
    internal const string ChoiceFile = "voice-clips.txt";
    internal const int MaximumClips = 5;
    internal const double MaximumSeconds = 8;
    private const int SampleRate = 16_000;
    private readonly object gate = new();
    private readonly string? directory;
    private readonly string? root;

    internal VoiceClips(string? directory)
    {
        this.directory = directory;
        root = directory is null ? null : Path.Combine(directory, Folder);
        try { Enabled = directory is null || File.ReadAllText(Path.Combine(directory, ChoiceFile)).Trim() != "off"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Enabled = true; }
    }

    internal bool Enabled { get; private set; }

    /// <summary>A voice keeps clips until the owner names it or marks it as theirs.</summary>
    internal static bool Wanted(KnownVoice voice) => voice is { Removed: false, Name: null, Owner: false };

    internal void SetEnabled(bool on)
    {
        if (directory is null) throw new InvalidOperationException("Martlet's data folder isn't available.");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ChoiceFile), on ? "on" : "off");
        Enabled = on;
        if (!on) DeleteAll();
    }

    /// <summary>The voice's clips, newest first.</summary>
    internal IReadOnlyList<VoiceClip> List(string voiceId)
    {
        if (FolderOf(voiceId) is not { } folder) return [];
        lock (gate)
        {
            try
            {
                if (!Directory.Exists(folder)) return [];
                return Directory.GetFiles(folder, "*.wav").Select(Read).OfType<VoiceClip>().OrderByDescending(c => c.At).ToArray();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
        }
    }

    /// <summary>How many clips are kept, over how many voices.</summary>
    internal (int Voices, int Clips) Count()
    {
        if (root is null) return (0, 0);
        lock (gate)
        {
            try
            {
                if (!Directory.Exists(root)) return (0, 0);
                var counts = Directory.GetDirectories(root).Select(d => Directory.GetFiles(d, "*.wav").Length).Where(n => n > 0).ToArray();
                return (counts.Length, counts.Sum());
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return (0, 0); }
        }
    }

    /// <summary>Keeps <paramref name="samples"/> (16 kHz mono, at most <see cref="MaximumSeconds"/> are kept) as the voice's
    /// newest clip, dropping the oldest beyond <see cref="MaximumClips"/>.</summary>
    internal void Save(string voiceId, ReadOnlySpan<float> samples, DateTimeOffset at)
    {
        if (!Enabled || FolderOf(voiceId) is not { } folder || samples.Length < SampleRate / 2) return;
        var bytes = Wave(samples[..Math.Min(samples.Length, (int)(MaximumSeconds * SampleRate))]);
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(folder);
                var name = at.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture);
                var path = Path.Combine(folder, name + ".wav");
                for (var n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{name}-{n}.wav");
                File.WriteAllBytes(path, bytes);
                foreach (var old in Directory.GetFiles(folder, "*.wav").OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(MaximumClips))
                    File.Delete(old);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ErrorLog.Warn("Keeping a voice clip failed.", error);
            }
        }
    }

    /// <summary>Moves a merged voice's clips to the voice it was merged into (the newest are kept).</summary>
    internal void Move(string fromId, string intoId)
    {
        if (FolderOf(fromId) is not { } from || FolderOf(intoId) is not { } into) return;
        lock (gate)
        {
            try
            {
                if (!Directory.Exists(from)) return;
                Directory.CreateDirectory(into);
                foreach (var file in Directory.GetFiles(from, "*.wav"))
                {
                    var target = Path.Combine(into, Path.GetFileName(file));
                    if (!File.Exists(target)) File.Move(file, target);
                }
                Directory.Delete(from, recursive: true);
                foreach (var old in Directory.GetFiles(into, "*.wav").OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(MaximumClips))
                    File.Delete(old);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ErrorLog.Warn("Moving voice clips failed.", error);
            }
        }
    }

    /// <summary>Deletes the clips of every voice that is named by the owner, theirs, forgotten or merged away.</summary>
    internal void Prune(VoiceRoster roster)
    {
        if (root is null) return;
        var keep = roster.Live.Where(Wanted).Select(v => v.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (gate)
        {
            try
            {
                if (!Directory.Exists(root)) return;
                foreach (var folder in Directory.GetDirectories(root))
                    if (!keep.Contains(Path.GetFileName(folder))) Directory.Delete(folder, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ErrorLog.Warn("Deleting voice clips failed.", error);
            }
        }
    }

    internal void DeleteAll()
    {
        if (root is null) return;
        lock (gate)
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ErrorLog.Warn("Deleting voice clips failed.", error);
            }
        }
    }

    // Voice IDs come from the shared list; only plain IDs name a folder.
    private string? FolderOf(string voiceId) =>
        root is not null && voiceId.Length is > 0 and <= 64 && voiceId.All(char.IsAsciiLetterOrDigit) ? Path.Combine(root, voiceId) : null;

    private static VoiceClip? Read(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.Length < 18 || !DateTime.TryParseExact(name[..18], "yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at))
            return null;
        var length = new FileInfo(path).Length;
        return new(path, new DateTimeOffset(at, TimeSpan.Zero), Math.Round(Math.Max(0, length - 44) / 2.0 / SampleRate, 1));
    }

    private static byte[] Wave(ReadOnlySpan<float> samples)
    {
        var bytes = new byte[44 + samples.Length * 2];
        var span = bytes.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], bytes.Length - 8);
        "WAVEfmt "u8.CopyTo(span[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], samples.Length * 2);
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(span[(44 + i * 2)..], (short)Math.Clamp(samples[i] * 32767f, -32768f, 32767f));
        return bytes;
    }
}
