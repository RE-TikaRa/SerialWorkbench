using System.Text.Json;
using System.Text.Json.Serialization;
using SerialWorkbench.Domain;

namespace SerialWorkbench.Protocols;

public static class SerialSequenceCodec
{
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new HexBytesConverter() },
    };

    public static SerialSequenceDefinition Deserialize(string json) =>
        JsonSerializer.Deserialize<SerialSequenceDefinition>(json, jsonOptions)
        ?? throw new InvalidDataException("Sequence definition is empty.");

    public static string Serialize(SerialSequenceDefinition sequence) => JsonSerializer.Serialize(sequence, jsonOptions);

    private sealed class HexBytesConverter : JsonConverter<byte[]>
    {
        public override byte[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            HexCodec.Parse(reader.GetString() ?? throw new JsonException("Sequence data must be a HEX string."));

        public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options) => writer.WriteStringValue(HexCodec.Format(value));
    }
}
