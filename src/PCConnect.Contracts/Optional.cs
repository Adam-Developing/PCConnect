using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCConnect.Contracts.V2;

/// <summary>Distinguishes an omitted JSON merge-patch property from an explicit null.</summary>
[JsonConverter(typeof(PatchValueJsonConverterFactory))]
public readonly record struct PatchValue<T>(bool IsSpecified, T? Value)
{
    public static implicit operator PatchValue<T>(T? value) => new(true, value);
}

public sealed class PatchValueJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(PatchValue<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(
            typeof(PatchValueJsonConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;

    private sealed class PatchValueJsonConverter<T> : JsonConverter<PatchValue<T>>
    {
        public override bool HandleNull => true;

        public override PatchValue<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(true, reader.TokenType == JsonTokenType.Null ? default : JsonSerializer.Deserialize<T>(ref reader, options));

        public override void Write(Utf8JsonWriter writer, PatchValue<T> value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.Value, options);
    }
}
