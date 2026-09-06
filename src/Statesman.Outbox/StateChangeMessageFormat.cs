using System.Text.Json;
using System.Text.Json.Serialization;

namespace Statesman.Outbox;

/// <summary>
/// The wire shape of an outbox message: a flattened, self-describing JSON object carrying one
/// <see cref="StateRecord"/> plus the store and declaration identity a broker consumer needs.
/// Versioned independently of <c>statesman.ledger-export/v1</c>, which is a file format free to
/// change for file-shape reasons a wire contract does not share.
/// </summary>
public static class StateChangeMessageFormat
{
    /// <summary>
    /// The format identifier stamped on every <see cref="StateChangeMessage.Format"/>. Bump it when
    /// the field shapes change incompatibly; the per-record <see cref="StateRecord.SchemaVersion"/>
    /// is unrelated and is carried verbatim.
    /// </summary>
    public const string Version = "statesman.state-change/v1";

    /// <summary>The serializer options a message is written and read with: camelCase, enums as names, nulls omitted. Read-only.</summary>
    public static JsonSerializerOptions Json { get; } = CreateJson();

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            Converters = { new JsonStringEnumConverter() },
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
