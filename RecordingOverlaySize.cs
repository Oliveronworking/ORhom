using System.Text.Json;
using System.Text.Json.Serialization;

namespace ORhom;

[JsonConverter(typeof(RecordingOverlaySizeJsonConverter))]
internal enum RecordingOverlaySize
{
    Small,
    Medium,
    Large
}

internal sealed class RecordingOverlaySizeJsonConverter : JsonConverter<RecordingOverlaySize>
{
    public override RecordingOverlaySize Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            return Enum.TryParse<RecordingOverlaySize>(value, ignoreCase: true, out var parsed) &&
                   Enum.IsDefined(parsed)
                ? parsed
                : RecordingOverlaySize.Small;
        }

        if (reader.TokenType == JsonTokenType.Number &&
            reader.TryGetInt32(out var numericValue))
        {
            var parsed = (RecordingOverlaySize)numericValue;
            return Enum.IsDefined(parsed)
                ? parsed
                : RecordingOverlaySize.Small;
        }

        using var ignoredValue = JsonDocument.ParseValue(ref reader);
        return RecordingOverlaySize.Small;
    }

    public override void Write(
        Utf8JsonWriter writer,
        RecordingOverlaySize value,
        JsonSerializerOptions options)
    {
        var normalized = Enum.IsDefined(value)
            ? value
            : RecordingOverlaySize.Small;
        writer.WriteStringValue(normalized.ToString());
    }
}
