using System.Text.Json;
using System.Text.Json.Serialization;

namespace Martlet.Core.Contracts;

public static class ContractJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            MaxDepth = 16,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            RespectNullableAnnotations = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static T Read<T>(ReadOnlyMemory<byte> json, int maximumBytes = ContractRules.MaxJsonBytes) where T : IContract
    {
        ContractRules.Require(maximumBytes is > 0 and <= ContractRules.MaxJsonBytes, "Invalid JSON size limit.");
        ContractRules.Require(json.Length <= maximumBytes, "JSON exceeds the supported size limit.", ErrorCode.PayloadTooLarge);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            RejectDuplicates(document.RootElement);
            var value = document.Deserialize<T>(Options);
            ContractRules.Require(value is not null, "A contract object is required.");
            value!.Validate();
            return value;
        }
        catch (JsonException)
        {
            // Do not echo malformed source values (which may contain private data).
            throw new ContractException(ErrorCode.InvalidContract, "JSON is malformed, missing required fields or contains unsupported values.");
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

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                ContractRules.Require(names.Add(property.Name), "Duplicate JSON properties are not supported.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                RejectDuplicates(item);
        }
    }
}
