using System.Text.Json;
using System.Text.Json.Serialization;

namespace Martlet.Core.Contracts;

internal sealed class ExactEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(ExactEnumConverter<>).MakeGenericType(typeToConvert))!;

    private sealed class ExactEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
    {
        private static readonly Dictionary<string, TEnum> Tokens = Enum.GetNames<TEnum>()
            .ToDictionary(JsonNamingPolicy.SnakeCaseLower.ConvertName, Enum.Parse<TEnum>, StringComparer.Ordinal);

        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String || reader.GetString() is not { } token ||
                !Tokens.TryGetValue(token, out var value))
                throw new JsonException("An exact declared enum token is required.");
            return value;
        }

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
        {
            var name = Enum.GetName(value);
            if (name is null)
                throw new JsonException("An undefined enum value cannot be written.");
            writer.WriteStringValue(JsonNamingPolicy.SnakeCaseLower.ConvertName(name));
        }
    }
}
