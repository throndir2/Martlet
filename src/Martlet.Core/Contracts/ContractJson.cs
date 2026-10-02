using System.Text.Json;
using System.Text.Json.Serialization;

namespace Martlet.Core.Contracts;

public static class ContractJson
{
    public const int DefaultMaxDepth = 16;
    public const int DeepMaxDepth = 64;
    private static readonly JsonSerializerOptions Options = CreateOptions(DefaultMaxDepth);
    // Provider events that echo nested tool schemas (the Responses API repeats the request's tools).
    private static readonly JsonSerializerOptions DeepOptions = CreateOptions(DeepMaxDepth);

    private static JsonSerializerOptions CreateOptions(int maxDepth)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            MaxDepth = maxDepth,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            RespectNullableAnnotations = true
        };
        options.Converters.Add(new ExactEnumConverterFactory());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static T Read<T>(ReadOnlyMemory<byte> json, int maximumBytes = ContractRules.MaxJsonBytes) where T : IContract =>
        Read<T>(json, maximumBytes, DefaultMaxDepth);

    public static T Read<T>(ReadOnlyMemory<byte> json, int maximumBytes, int maxDepth) where T : IContract
    {
        ContractRules.Require(maximumBytes is > 0 and <= ContractRules.MaxJsonBytes, "Invalid JSON size limit.");
        ContractRules.Require(maxDepth is DefaultMaxDepth or DeepMaxDepth, "Invalid JSON depth limit.");
        ContractRules.Require(json.Length <= maximumBytes, "JSON exceeds the supported size limit.", ErrorCode.PayloadTooLarge);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = maxDepth });
            InspectJson(document.RootElement);
            var value = document.Deserialize<T>(maxDepth == DeepMaxDepth ? DeepOptions : Options);
            ContractRules.Require(value is not null, "A contract object is required.");
            value!.Validate();
            return value;
        }
        catch (JsonException)
        {
            throw MalformedJson();
        }
    }

    public static byte[] Write<T>(T value, int maximumBytes = ContractRules.MaxJsonBytes) where T : IContract
    {
        ContractRules.Require(maximumBytes is > 0 and <= ContractRules.MaxJsonBytes, "Invalid JSON size limit.");
        ArgumentNullException.ThrowIfNull(value);
        value.Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        ContractRules.Require(bytes.Length <= maximumBytes, "JSON exceeds the supported size limit.", ErrorCode.PayloadTooLarge);
        return bytes;
    }

    internal static void InspectJson(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                string name;
                try
                {
                    name = property.Name;
                }
                catch (InvalidOperationException)
                {
                    // JsonDocument defers UTF-8/escaped-surrogate decoding until Name is read.
                    throw MalformedJson();
                }
                ContractRules.Require(names.Add(name), "Duplicate JSON properties are not supported.");
                InspectJson(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                InspectJson(item);
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            try
            {
                // Inspect ignored optional strings too, before any typed converter is invoked.
                _ = element.GetString();
            }
            catch (InvalidOperationException)
            {
                throw MalformedJson();
            }
        }
    }

    private static ContractException MalformedJson() =>
        new(ErrorCode.InvalidContract, "JSON is malformed, missing required fields or contains unsupported values.");
}
