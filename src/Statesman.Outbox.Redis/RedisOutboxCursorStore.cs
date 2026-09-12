using System.Globalization;
using StackExchange.Redis;

namespace Statesman.Outbox.Redis;

/// <summary>Options for <see cref="RedisOutboxCursorStore"/>.</summary>
public sealed class RedisOutboxCursorStoreOptions
{
    /// <summary>The key namespace. A cursor key is <c>{KeyPrefix}:outbox-cursor:{outboxId}</c>.</summary>
    public string KeyPrefix { get; set; } = "statesman";

    /// <summary>The Redis database index; -1 uses the connection's default.</summary>
    public int Database { get; set; } = -1;
}

/// <summary>
/// An <see cref="IOutboxCursorStore"/> keeping each outbox's position in a Redis string key,
/// advanced by a Lua script that never lowers it.
/// </summary>
/// <remarks>
/// <para>
/// The script compares canonical decimal strings by length, then lexicographically — deliberately
/// not <c>tonumber</c>, which is an IEEE double in Lua and loses integer precision above 2^53. The
/// filesystem provider allocates positions from UTC ticks (about 6.4e17), well past that, so a
/// numeric comparison would silently fail to advance the cursor. This is the same shape, and the
/// same reasoning, as <c>RedisStateLedgerStore.AdvanceGlobalPositionScript</c>.
/// </para>
/// <para>
/// <see cref="ReadAsync"/> and <see cref="WriteAsync"/> honour a cancelled token only at method
/// entry. Measured against StackExchange.Redis 3.1.31: neither <c>IDatabaseAsync.StringGetAsync</c>
/// nor <c>IDatabaseAsync.ScriptEvaluateAsync</c> has a <see cref="CancellationToken"/> overload, so
/// there is no way to cancel the in-flight command itself. Wrapping either call in
/// <c>Task.WaitAsync(cancellationToken)</c> would abandon rather than cancel it — the write could
/// still land after the caller gave up — which is a behaviour change with no measured benefit here,
/// so the entry-only check stays. ROADMAP 0.3 Phase 15 answered it as a contract rather than a
/// wrapper, addendum decision 48.
/// </para>
/// </remarks>
public sealed class RedisOutboxCursorStore : IOutboxCursorStore
{
    // Raises the cursor to at least ARGV[1] without ever lowering it. Compares canonical decimal
    // strings rather than Lua numbers, which are IEEE doubles and lose integer precision above 2^53;
    // both operands are non-negative canonical decimals because this key is only ever written here.
    private const string AdvanceCursorScript = """
        local current = redis.call('get', KEYS[1])
        if not current then current = '0' end
        local target = ARGV[1]
        if #current < #target or (#current == #target and current < target) then
            redis.call('set', KEYS[1], target)
        end
        return 1
        """;

    private readonly IDatabase _database;
    private readonly RedisOutboxCursorStoreOptions _options;

    /// <summary>Creates the store over an existing connection. The connection is not owned and is not disposed.</summary>
    public RedisOutboxCursorStore(IConnectionMultiplexer connection, RedisOutboxCursorStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _options = options ?? new RedisOutboxCursorStoreOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.KeyPrefix);
        _database = connection.GetDatabase(_options.Database);
    }

    /// <inheritdoc />
    public async ValueTask<StateChangeCursor?> ReadAsync(string outboxId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outboxId);
        cancellationToken.ThrowIfCancellationRequested();

        RedisValue stored = await _database.StringGetAsync(CursorKey(outboxId)).ConfigureAwait(false);
        if (stored.IsNullOrEmpty)
        {
            return null;
        }

        // RedisValue converts implicitly to both string and ReadOnlySpan<byte>, so long.Parse is
        // ambiguous without the cast. Do not remove it.
        return long.TryParse((string)stored!, NumberStyles.None, CultureInfo.InvariantCulture, out long position) && position > 0
            ? new StateChangeCursor(position)
            : null;
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(string outboxId, StateChangeCursor cursor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outboxId);
        cancellationToken.ThrowIfCancellationRequested();

        await _database.ScriptEvaluateAsync(
            AdvanceCursorScript,
            [CursorKey(outboxId)],
            [(RedisValue)cursor.Position.ToString(CultureInfo.InvariantCulture)]).ConfigureAwait(false);
    }

    private RedisKey CursorKey(string outboxId) => $"{_options.KeyPrefix}:outbox-cursor:{outboxId}";
}
