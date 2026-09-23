using System.Text;
using System.Text.Json;

namespace Martlet.Updates;

internal static class PayloadMetadata
{
    internal const int MaximumV2Bytes = 16 * 1024 * 1024;
    internal const string Channel = "INTERNAL DEVELOPMENT ONLY - UNSIGNED";

    internal static void CheckLength(PayloadFile file)
    {
        if (file.Path is "manifest.json" or "SHA256SUMS.txt" or "sbom.cdx.json" &&
            file.Bytes > MaximumV2Bytes)
            throw new StagingException(StagingFailure.CapacityExceeded);
    }

    internal static void Verify(CandidateManifest candidate, Func<string, Stream> open, CancellationToken token)
    {
        foreach (var file in candidate.Files) CheckLength(file);
        var files = candidate.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        byte[] Read(string name)
        {
            using var input = open(name);
            var file = files[name];
            var bytes = BoundedIo.Read(input, MaximumV2Bytes, token);
            if (bytes.LongLength != file.Bytes || Wire.Hash(bytes) != file.Sha256)
                throw new StagingException(StagingFailure.CorruptArchive);
            return bytes;
        }
        var expected = candidate.Files.Where(f => f.Path is not ("manifest.json" or "SHA256SUMS.txt"))
            .Select(f => f with { Path = f.Path.Replace('/', '\\') }).OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
        var facts = Manifest(Read("manifest.json"), candidate, expected, token);
        if (facts.Schema == 1 && files["SHA256SUMS.txt"].Bytes > Wire.MaximumManifestBytes)
            throw new StagingException(StagingFailure.CapacityExceeded);
        using (var sums = open("SHA256SUMS.txt"))
        {
            foreach (var file in expected) Match(sums, $"{file.Sha256}  {file.Path}\n", token);
            Match(sums, $"{facts.Hash}  manifest.json\n", token);
            token.ThrowIfCancellationRequested();
            if (sums.ReadByte() != -1) throw new StagingException(StagingFailure.InvalidManifest);
        }
        if (facts.Provenance is { } provenance)
            PayloadSbom.Verify(Read("sbom.cdx.json"), candidate.ApplicationVersion, expected, provenance, token);
        token.ThrowIfCancellationRequested();
    }

    private sealed record Facts(int Schema, string Hash, PayloadProvenance.Facts? Provenance);

    private static Facts Manifest(byte[] bytes, CandidateManifest candidate, PayloadFile[] expected, CancellationToken token)
    {
        using var document = Wire.ReadDocument(bytes, MaximumV2Bytes, token);
        var r = new EvidenceReader(token);
        var root = document.RootElement;
        var schema = r.Integer(r.Member(root, "schemaVersion"));
        if (schema is not (1 or 2 or 3)) throw new StagingException(StagingFailure.IncompatibleFormat);
        if (schema == 1 && bytes.Length > Wire.MaximumManifestBytes)
            throw new StagingException(StagingFailure.CapacityExceeded);
        r.Keys(root, "schemaVersion channel applicationVersion rid sdkVersion runtimeVersion sourceCommit sourceDirty files" +
            (schema >= 2 ? " provenance" : ""));
        r.Equal(r.Text(root, "channel"), Channel);
        r.Equal(r.Text(root, "applicationVersion", 48), candidate.ApplicationVersion);
        r.Equal(r.Text(root, "rid", 48), candidate.Rid);
        var sdk = r.Text(root, "sdkVersion", 48);
        var runtime = r.Text(root, "runtimeVersion", 48);
        r.Require(Version.TryParse(sdk, out _) && Version.TryParse(runtime, out _));
        var source = r.Hex(r.Member(root, "sourceCommit"), 40);
        var dirty = r.Boolean(r.Member(root, "sourceDirty"));
        var records = r.Items(r.Member(root, "files"), 8192).ToArray();
        r.Require(records.Length == expected.Length);
        for (var index = 0; index < records.Length; index++)
        {
            var file = r.File(records[index]);
            r.Require(file.Path == expected[index].Path && file.Bytes == expected[index].Bytes && file.Hash == expected[index].Sha256);
        }
        var sbom = expected.Any(f => f.Path == "sbom.cdx.json");
        r.Require(sbom == (schema >= 2));
        var provenance = schema >= 2
            ? PayloadProvenance.Read(r.Member(root, "provenance"), source, dirty, sdk, runtime, expected, r, (int)schema - 1)
            : null;
        return new((int)schema, Wire.Hash(bytes), provenance);
    }

    private static void Match(Stream input, string expected, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(expected);
        var buffer = new byte[bytes.Length];
        var offset = 0;
        while (offset < buffer.Length)
        {
            token.ThrowIfCancellationRequested();
            var read = input.Read(buffer, offset, buffer.Length - offset);
            if (read == 0) throw new StagingException(StagingFailure.InvalidManifest);
            offset += read;
        }
        if (!bytes.AsSpan().SequenceEqual(buffer)) throw new StagingException(StagingFailure.InvalidManifest);
    }
}

internal sealed class EvidenceReader(CancellationToken token)
{
    internal void Require(bool condition)
    {
        token.ThrowIfCancellationRequested();
        if (!condition) throw new StagingException(StagingFailure.InvalidManifest);
    }

    internal void Equal(string actual, string expected) => Require(actual == expected);

    internal void Keys(JsonElement value, string required, string optional = "")
    {
        Require(value.ValueKind == JsonValueKind.Object);
        var names = required.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var allowed = names.Concat(optional.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToHashSet(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) Require(allowed.Remove(property.Name));
        foreach (var name in names) Require(value.TryGetProperty(name, out _));
    }

    internal JsonElement Member(JsonElement value, string name)
    {
        Require(value.ValueKind == JsonValueKind.Object);
        if (!value.TryGetProperty(name, out var result)) throw new StagingException(StagingFailure.InvalidManifest);
        return result;
    }

    internal string Text(JsonElement parent, string name, int maximum = 1024) => Text(Member(parent, name), maximum);

    internal string Text(JsonElement value, int maximum = 1024, bool empty = false)
    {
        Require(value.ValueKind == JsonValueKind.String);
        string result;
        try { result = value.GetString()!; }
        catch (InvalidOperationException) { throw new StagingException(StagingFailure.InvalidManifest); }
        if (result.Length > maximum) throw new StagingException(StagingFailure.CapacityExceeded);
        Require((empty || result.Length != 0) && !result.Any(char.IsControl));
        return result;
    }

    internal long Integer(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.Number);
        if (!value.TryGetInt64(out var number)) throw new StagingException(StagingFailure.InvalidManifest);
        return number;
    }

    internal bool Boolean(JsonElement value)
    {
        Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False);
        return value.GetBoolean();
    }

    internal void Schema(JsonElement value, long expected)
    {
        if (Integer(value) != expected) throw new StagingException(StagingFailure.IncompatibleFormat);
    }

    internal string Hex(JsonElement value, int length = 64)
    {
        var text = Text(value, length);
        Require(Wire.IsHex(text, length));
        return text;
    }

    internal string ContentHash(JsonElement value)
    {
        var text = Text(value, 88);
        Span<byte> bytes = stackalloc byte[64];
        Require(Convert.TryFromBase64String(text, bytes, out var count) && count == 64 &&
            Convert.ToBase64String(bytes) == text);
        return text;
    }

    internal IEnumerable<JsonElement> Items(JsonElement value, int maximum, int minimum = 0)
    {
        Require(value.ValueKind == JsonValueKind.Array);
        if (value.GetArrayLength() > maximum) throw new StagingException(StagingFailure.CapacityExceeded);
        Require(value.GetArrayLength() >= minimum);
        foreach (var item in value.EnumerateArray())
        {
            Require(item.ValueKind != JsonValueKind.Null);
            yield return item;
        }
    }

    internal string Path(JsonElement value)
    {
        return Path(Text(value));
    }

    internal string Path(string path)
    {
        if (path.Length > 1024) throw new StagingException(StagingFailure.CapacityExceeded);
        Require(path.Length != 0 && !path.Any(c => char.IsControl(c) || c is '<' or '>' or ':' or '"' or '/' or '|' or '?' or '*'));
        foreach (var part in path.Split('\\'))
        {
            Require(part.Length != 0 && part is not ("." or "..") && !part.EndsWith('.') && !part.EndsWith(' '));
            var stem = part.Split('.')[0].ToUpperInvariant();
            Require(stem is not ("CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" or "CONIN$" or "CONOUT$") &&
                !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
                    stem.StartsWith("LPT", StringComparison.Ordinal)) && "123456789\u00b9\u00b2\u00b3".Contains(stem[3])));
        }
        return path;
    }

    internal sealed record FileRecord(string Path, long Bytes, string? Hash);
    internal FileRecord File(JsonElement value, bool missing = false)
    {
        Keys(value, "path bytes sha256");
        var path = Path(Member(value, "path"));
        var bytes = Integer(Member(value, "bytes"));
        Require(bytes >= 0);
        var hash = Member(value, "sha256");
        if (hash.ValueKind == JsonValueKind.Null)
        {
            Require(missing && bytes == 0);
            return new(path, bytes, null);
        }
        return new(path, bytes, Hex(hash));
    }

    internal string[] Edges(JsonElement value, IReadOnlySet<string> known, int maximum = 2048)
    {
        var edges = Items(value, maximum).Select(e => Text(e, 264)).ToArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var edge in edges) Require(known.Contains(edge) && seen.Add(edge));
        return edges;
    }
}
