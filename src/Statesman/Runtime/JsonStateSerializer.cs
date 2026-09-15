using System.Text.Json;
using System.Text.Json.Serialization;

namespace Statesman;

public sealed class JsonStateSerializer : IStateSerializer
{
    public JsonStateSerializer(JsonSerializerOptions? options = null)
    {
        Options = options ?? CreateDefaultOptions();
    }

    /// <summary>
    /// The identity this serializer stamps into every envelope it writes. A persisted contract:
    /// records already in a consumer's store carry this exact string, so it is never renamed, and a
    /// future incompatible change to what this type emits ships as a new id rather than as a
    /// redefinition of this one.
    /// </summary>
    public const string Id = "statesman.json/v1";

    /// <summary>The media type this serializer stamps into every envelope it writes.</summary>
    public const string MediaType = "application/json";

    public JsonSerializerOptions Options { get; }

    /// <inheritdoc />
    public string SerializerId => Id;

    /// <inheritdoc />
    public string ContentType => MediaType;

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
