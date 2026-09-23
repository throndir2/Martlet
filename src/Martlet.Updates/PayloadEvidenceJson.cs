using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Martlet.Updates;

internal static class PayloadEvidenceJson
{
    internal static string Hash(JsonElement value, EvidenceReader reader) => Wire.Hash(Encode(value, reader));

    internal static string HashArray(IEnumerable<JsonElement> values, EvidenceReader reader)
    {
        var output = new StringBuilder();
        Array(values.ToArray(), output, 0, reader);
        return Wire.Hash(Finish(output, reader));
    }

    internal static byte[] Encode(JsonElement value, EvidenceReader reader)
    {
        var output = new StringBuilder();
        Write(value, output, 0, reader);
        return Finish(output, reader);
    }

    private static byte[] Finish(StringBuilder output, EvidenceReader reader)
    {
        output.Append('\n');
        var bytes = Encoding.UTF8.GetBytes(output.ToString());
        reader.Require(bytes.Length <= PayloadMetadata.MaximumV2Bytes);
        return bytes;
    }

    private static void Indent(StringBuilder output, int depth) => output.Append(' ', depth * 2);

    private static void Write(JsonElement value, StringBuilder output, int depth, EvidenceReader reader)
    {
        reader.Require(depth <= 16 && output.Length <= PayloadMetadata.MaximumV2Bytes);
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
                reader.Require(properties.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == properties.Length);
                output.Append('{');
                for (var index = 0; index < properties.Length; index++)
                {
                    output.Append(index == 0 ? "\n" : ",\n");
                    Indent(output, depth + 1);
                    String(properties[index].Name, output);
                    output.Append(": ");
                    Write(properties[index].Value, output, depth + 1, reader);
                }
                if (properties.Length != 0) { output.Append('\n'); Indent(output, depth); }
                output.Append('}');
                break;
            case JsonValueKind.Array:
                Array(value.EnumerateArray().ToArray(), output, depth, reader);
                break;
            case JsonValueKind.String:
                String(value.GetString()!, output);
                break;
            case JsonValueKind.Number:
                output.Append(reader.Integer(value).ToString(CultureInfo.InvariantCulture));
                break;
            case JsonValueKind.True: output.Append("true"); break;
            case JsonValueKind.False: output.Append("false"); break;
            case JsonValueKind.Null: output.Append("null"); break;
            default: reader.Require(false); break;
        }
    }

    private static void Array(JsonElement[] values, StringBuilder output, int depth, EvidenceReader reader)
    {
        output.Append('[');
        for (var index = 0; index < values.Length; index++)
        {
            output.Append(index == 0 ? "\n" : ",\n");
            Indent(output, depth + 1);
            Write(values[index], output, depth + 1, reader);
        }
        if (values.Length != 0) { output.Append('\n'); Indent(output, depth); }
        output.Append(']');
    }

    private static void String(string value, StringBuilder output)
    {
        output.Append('"');
        foreach (var character in value)
        {
            output.Append(character switch
            {
                '"' => "\\\"", '\\' => "\\\\", '\b' => "\\b", '\f' => "\\f",
                '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
                < ' ' or '\u0085' or '\u2028' or '\u2029' => "\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture),
                _ => character.ToString()
            });
        }
        output.Append('"');
    }
}
