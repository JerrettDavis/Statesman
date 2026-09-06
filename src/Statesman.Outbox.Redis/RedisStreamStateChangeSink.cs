using System.Globalization;
using System.Text.Json;
using StackExchange.Redis;

namespace Statesman.Outbox.Redis;

/// <summary>Options for <see cref="RedisStreamStateChangeSink"/>.</summary>
public sealed class RedisStreamStateChangeSinkOptions
{
    /// <summary>The key namespace. The stream key is <c>{KeyPrefix}:{StreamName}</c>.</summary>
    public string KeyPrefix { get; set; } = "statesman";

    /// <summary>The stream's name within the prefix.</summary>
    public string StreamName { get; set; } = "outbox";

    /// <summary>The Redis database index; -1 uses the connection's default.</summary>
    public int Database { get; set; } = -1;

    /// <summary>Trim the stream to about this many entries on every publish. Null never trims. Trimming discards entries a slow consumer may not have read.</summary>
    public long? MaxLength { get; set; }

    /// <summary>Let Redis trim approximately (much cheaper) rather than exactly. Ignored when <see cref="MaxLength"/> is null.</summary>
    public bool UseApproximateMaxLength { get; set; } = true;
}

/// <summary>
/// Publishes outbox messages to a Redis stream, one <c>XADD</c> per message with the explicit entry
/// id <c>{GlobalPosition}-0</c>.
/// </summary>
/// <remarks>
/// <para>
/// The explicit id is the deduplication mechanism: Redis refuses an <c>XADD</c> whose id is not
/// greater than the stream's top entry, so a re-publish after a crash is rejected server-side rather
/// than duplicated. That rejection arrives as a <see cref="RedisServerException"/> whose message
/// contains <c>equal or smaller</c>; on that rejection this sink reads the entry already at that id
/// and compares its <c>messageId</c> field to the message being published. Equal means this really is
/// the same message republished — it is counted on <see cref="Deduplicated"/> and the batch
/// continues. <see cref="StateRecord.GlobalPosition"/> is unique only within one store's position
/// lineage, so a missing or different <c>messageId</c> means the rejected position belongs to a different lineage
/// sharing this stream key — that throws <see cref="InvalidOperationException"/> naming both messages
/// rather than silently discarding one, because a stream key must be exclusive to one store's
/// position lineage. Any other Redis error also propagates, so the dispatcher does not advance its
/// cursor.
/// </para>
/// <para>
/// Entry ids are two unsigned 64-bit integers, so every <see cref="StateRecord.GlobalPosition"/>
/// fits exactly — including the filesystem provider's UTC-tick positions. The 2^52 bound
/// <c>RedisStateLedgerStore.MaxImportablePosition</c> imposes is about sorted-set scores and does not
/// apply here.
/// </para>
/// <para>
/// Consumers read with <c>XREAD</c> or a consumer group. Because delivery is at-least-once and this
/// sink deduplicates only against the stream's current top entry, a consumer that must be exactly
/// once should key on the <c>messageId</c> field.
/// </para>
/// </remarks>
public sealed class RedisStreamStateChangeSink : IStateChangeSink
{
    private const string DuplicateIdFragment = "equal or smaller";

    private readonly IDatabase _database;
    private readonly RedisStreamStateChangeSinkOptions _options;

    /// <summary>Creates the sink over an existing connection. The connection is not owned and is not disposed.</summary>
    public RedisStreamStateChangeSink(IConnectionMultiplexer connection, RedisStreamStateChangeSinkOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _options = options ?? new RedisStreamStateChangeSinkOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.KeyPrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.StreamName);
        _database = connection.GetDatabase(_options.Database);
        StreamKey = $"{_options.KeyPrefix}:{_options.StreamName}";
    }

    /// <summary>The Redis key this sink writes to.</summary>
    public RedisKey StreamKey { get; }

    /// <inheritdoc />
    public string Name => StreamKey.ToString();

    /// <summary>How many messages Redis rejected as already present, cumulative since construction.</summary>
    public long Deduplicated { get; private set; }

    /// <inheritdoc />
    public async ValueTask PublishAsync(IReadOnlyList<StateChangeMessage> batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

        foreach (StateChangeMessage message in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RedisValue entryId = EntryId(message.GlobalPosition);
            try
            {
                await _database.StreamAddAsync(
                    StreamKey,
                    Fields(message),
                    messageId: entryId,
                    maxLength: _options.MaxLength,
                    useApproximateMaxLength: _options.UseApproximateMaxLength,
                    limit: null,
                    trimMode: StreamTrimMode.KeepReferences,
                    flags: CommandFlags.None).ConfigureAwait(false);
            }
            catch (RedisServerException exception)
                when (exception.Message.Contains(DuplicateIdFragment, StringComparison.Ordinal))
            {
                // Redis rejected this id as not greater than the stream's top entry. That is a
                // benign duplicate only if the entry already there is THIS message republished —
                // not merely a message at the same position from a different store's lineage.
                // GlobalPosition is unique only within one store's position lineage, so two stores
                // sharing a stream key can otherwise collide here and have the second store's
                // genuinely-undelivered message counted as a duplicate and silently dropped.
                StreamEntry[] existing = await _database
                    .StreamRangeAsync(StreamKey, entryId, entryId, count: 1)
                    .ConfigureAwait(false);
                string? existingMessageId = existing.Length > 0 ? (string?)existing[0]["messageId"] : null;

                if (existing.Length > 0 && string.Equals(existingMessageId, message.MessageId, StringComparison.Ordinal))
                {
                    Deduplicated++;
                    continue;
                }

                string existingStore = existing.Length > 0 ? (string?)existing[0]["store"] ?? "<unknown>" : "<missing>";
                throw new InvalidOperationException(
                    $"Stream key '{StreamKey}' already has an entry at position {message.GlobalPosition} " +
                    $"(store '{existingStore}', messageId '{existingMessageId ?? "<missing>"}') that does not match " +
                    $"the message being published (messageId '{message.MessageId}'). A stream key must be exclusive " +
                    "to one store's position lineage — give each store's outbox its own StreamName or KeyPrefix.");
            }
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static RedisValue EntryId(long globalPosition) =>
        globalPosition.ToString(CultureInfo.InvariantCulture) + "-0";

    private static NameValueEntry[] Fields(StateChangeMessage message)
    {
        var fields = new List<NameValueEntry>(21)
        {
            new("format", message.Format),
            new("messageId", message.MessageId),
            new("store", message.Store),
            new("root", message.Root),
            new("path", message.Path),
            new("partition", message.Partition),
            new("address", message.Address),
            new("revision", message.Revision.ToString(CultureInfo.InvariantCulture)),
            new("globalPosition", message.GlobalPosition.ToString(CultureInfo.InvariantCulture)),
            new("occurredAt", message.OccurredAt.ToString("O", CultureInfo.InvariantCulture)),
            new("operation", message.Operation.ToString()),
            new("status", message.Status.ToString()),
            new("valueType", message.ValueType),
            new("schemaVersion", message.SchemaVersion.ToString(CultureInfo.InvariantCulture)),
            new("source", message.Source),
        };

        if (message.Fingerprint is { } fingerprint)
        {
            fields.Add(new NameValueEntry("fingerprint", fingerprint));
        }

        if (message.ContentType is { } contentType)
        {
            fields.Add(new NameValueEntry("contentType", contentType));
        }

        if (message.Payload is { } payload)
        {
            fields.Add(new NameValueEntry("payload", payload));
        }

        if (message.CorrelationId is { } correlationId)
        {
            fields.Add(new NameValueEntry("correlationId", correlationId));
        }

        if (message.CausationId is { } causationId)
        {
            fields.Add(new NameValueEntry("causationId", causationId));
        }

        if (message.Metadata.Count > 0)
        {
            fields.Add(new NameValueEntry(
                "metadata",
                JsonSerializer.Serialize(message.Metadata, StateChangeMessageFormat.Json)));
        }

        if (message.Error is { } error)
        {
            fields.Add(new NameValueEntry(
                "error",
                JsonSerializer.Serialize(error, StateChangeMessageFormat.Json)));
        }

        return fields.ToArray();
    }
}
