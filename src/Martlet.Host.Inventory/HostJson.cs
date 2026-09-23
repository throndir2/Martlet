using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Martlet.Host.Inventory;

public static class HostJson
{
    public const int MaximumBytes = 262_144;
    public const int MaximumDepth = 16;
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            NewLine = "\r\n",
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = MaximumDepth,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            TypeInfoResolver = HostJsonContext.Default
        };
        options.Converters.Add(new ExactEnumConverter<DoctorScope>());
        options.Converters.Add(new ExactEnumConverter<Provenance>());
        options.Converters.Add(new ExactEnumConverter<ProbeId>());
        options.Converters.Add(new ExactEnumConverter<FindingCode>());
        options.Converters.Add(new ExactEnumConverter<FindingState>());
        options.Converters.Add(new ExactEnumConverter<ActionId>());
        options.Converters.Add(new ExactEnumConverter<Distro>());
        options.Converters.Add(new ExactEnumConverter<KernelFlavor>());
        options.Converters.Add(new ExactEnumConverter<ExecutionContext>());
        options.Converters.Add(new ExactEnumConverter<BinaryPresence>());
        options.Converters.Add(new ExactEnumConverter<PackagePresence>());
        options.Converters.Add(new ExactEnumConverter<ToolKind>());
        options.Converters.Add(new ExactEnumConverter<ConstraintState>());
        options.MakeReadOnly();
        return options;
    }

    public static string Serialize(HostReport report, int maximumBytes = MaximumBytes)
    {
        CheckLimit(maximumBytes);
        ArgumentNullException.ThrowIfNull(report);
        report.Validate();
        var json = JsonSerializer.Serialize(report, ReportType);
        if (Encoding.UTF8.GetByteCount(json) > maximumBytes)
            throw new InvalidDataException("Host report exceeds the supported byte limit.");
        return json;
    }

    public static HostReport Deserialize(ReadOnlyMemory<byte> json, int maximumBytes = MaximumBytes)
    {
        CheckLimit(maximumBytes);
        if (json.IsEmpty || json.Length > maximumBytes)
            throw new InvalidDataException("Host report exceeds the supported byte limit or is empty.");
        // Own the bounded input so validation and deserialization cannot observe different bytes.
        var owned = json.ToArray();
        try
        {
            using var document = JsonDocument.Parse(owned, new JsonDocumentOptions { MaxDepth = MaximumDepth });
            Inspect(document.RootElement);
            var report = document.Deserialize(ReportType) ?? throw Malformed();
            report.Validate();
            using var canonical = JsonDocument.Parse(Serialize(report));
            // Get-only derived properties are not validated by System.Text.Json deserialization.
            if (!JsonElement.DeepEquals(document.RootElement, canonical.RootElement)) throw Malformed();
            return report;
        }
        catch (JsonException)
        {
            throw Malformed();
        }
        catch (InvalidOperationException)
        {
            // JsonDocument can defer invalid UTF-8/surrogate decoding until Name/GetString.
            throw Malformed();
        }
    }

    private static void CheckLimit(int maximumBytes)
    {
        if (maximumBytes is <= 0 or > MaximumBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
    }

    private static void Inspect(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Malformed();
                Inspect(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) Inspect(item);
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            _ = value.GetString();
        }
    }

    private static InvalidDataException Malformed() => new("Invalid, inconsistent or unsupported host report JSON.");
    internal static JsonTypeInfo<HostReport> ReportType => (JsonTypeInfo<HostReport>)Options.GetTypeInfo(typeof(HostReport));
    internal static JsonTypeInfo<CandidateManifest> ManifestType => (JsonTypeInfo<CandidateManifest>)Options.GetTypeInfo(typeof(CandidateManifest));

    private sealed class ExactEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String && Enum.TryParse<T>(reader.GetString(), out var value) &&
            Enum.IsDefined(value) && value.ToString() == reader.GetString()
                ? value : throw new JsonException("Unknown host contract enum.");
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            if (!Enum.IsDefined(value)) throw new JsonException("Unknown host contract enum.");
            writer.WriteStringValue(value.ToString());
        }

    }
}

[JsonSerializable(typeof(HostReport))]
[JsonSerializable(typeof(CandidateManifest))]
internal partial class HostJsonContext : JsonSerializerContext;
