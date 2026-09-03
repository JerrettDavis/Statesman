using System.Text.Json;
using System.Text.Json.Serialization;

namespace Statesman;

public sealed class JsonStateSerializer : IStateSerializer
{
    public JsonStateSerializer(JsonSerializerOptions? options = null)
    {
        Options = options ?? CreateDefaultOptions();
    }

    public JsonSerializerOptions Options { get; }

    public byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public byte[] Serialize(object value, Type type) => JsonSerializer.SerializeToUtf8Bytes(value, type, Options);

    public T? Deserialize<T>(ReadOnlySpan<byte> payload) => JsonSerializer.Deserialize<T>(payload, Options);

    public object? Deserialize(ReadOnlySpan<byte> payload, Type type) => JsonSerializer.Deserialize(payload, type, Options);

    private static JsonSerializerOptions CreateDefaultOptions() => new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}
