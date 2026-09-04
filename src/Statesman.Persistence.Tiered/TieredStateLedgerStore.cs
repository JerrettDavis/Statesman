using System.Runtime.CompilerServices;

namespace Statesman;

public enum TieredStateReadMode
{
    /// <summary>Read the cold authority and repair the hot replica before returning.</summary>
    ValidateCold = 0,

    /// <summary>Return a hot record immediately and consult cold storage only on a cache miss.</summary>
    PreferHot = 1,
}

public sealed class TieredStateLedgerStoreOptions
{
    public TieredStateReadMode ReadMode { get; set; } = TieredStateReadMode.ValidateCold;

    public bool ServeHotWhenColdUnavailable { get; set; }
}

public sealed class TieredStateLedgerStore : IStateLedgerStore, IStateCapabilityProvider, IStateChangeFeed
{
    private readonly IStateLedgerStore _hot;
    private readonly IStateLedgerReplica _hotReplica;
    private readonly IStateLedgerStore _cold;
    private readonly TieredStateLedgerStoreOptions _options;
    private readonly bool _ownsStores;

    public TieredStateLedgerStore(
        string name,
        IStateLedgerStore hot,
        IStateLedgerStore cold,
        TieredStateLedgerStoreOptions? options = null,
        bool ownsStores = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        _hot = hot ?? throw new ArgumentNullException(nameof(hot));
        _hotReplica = hot as IStateLedgerReplica
            ?? throw new ArgumentException("The hot store must implement IStateLedgerReplica.", nameof(hot));
        _cold = cold ?? throw new ArgumentNullException(nameof(cold));
        _options = options ?? new TieredStateLedgerStoreOptions();
        _ownsStores = ownsStores;
    }

    public string Name { get; }

    public Exception? LastCacheError { get; private set; }

    public async ValueTask<StateRecord?> ReadLatestAsync(
        StateAddress address,
        CancellationToken cancellationToken = default)
    {
        address.Validate();
        if (_options.ReadMode == TieredStateReadMode.PreferHot)
        {
            StateRecord? cached = await TryReadHotAsync(address, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                return cached;
            }
        }

        try
        {
            StateRecord? authoritative = await _cold.ReadLatestAsync(address, cancellationToken).ConfigureAwait(false);
            if (authoritative is not null)
            {
                await TryImportAsync(authoritative, cancellationToken).ConfigureAwait(false);
            }

            return authoritative;
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException &&
            _options.ServeHotWhenColdUnavailable)
        {
            StateRecord? cached = await TryReadHotAsync(address, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                return cached;
            }

            throw;
        }
    }

    public async IAsyncEnumerable<StateRecord> ReadHistoryAsync(
        StateAddress address,
        StateHistoryOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        address.Validate();
        options.Validate();
        await foreach (StateRecord record in _cold.ReadHistoryAsync(address, options, cancellationToken).ConfigureAwait(false))
        {
            yield return record;
        }
    }

    public IAsyncEnumerable<StateChangeEnvelope> ReadAsync(
        StateChangeCursor? from, CancellationToken cancellationToken = default)
    {
        if (_cold.TryGetCapability(out IStateChangeFeed? feed))
        {
            return feed.ReadAsync(from, cancellationToken);
        }

        throw new NotSupportedException("The cold store does not implement IStateChangeFeed.");
    }

    public async ValueTask<StateAppendResult> AppendAsync(
        StateAddress address,
        StateWriteCondition condition,
        StateCommit commit,
        CancellationToken cancellationToken = default)
    {
        address.Validate();
        condition.Validate();
        commit.Validate();
        StateAppendResult result = await _cold.AppendAsync(address, condition, commit, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded && result.Record is not null)
        {
            await TryImportAsync(result.Record, cancellationToken).ConfigureAwait(false);
        }
        else if (result.Current is not null)
        {
            await TryImportAsync(result.Current, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public async ValueTask PruneAsync(
        StateAddress address,
        StateRetentionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        address.Validate();
        policy.Validate();
        await _cold.PruneAsync(address, policy, cancellationToken).ConfigureAwait(false);
        try
        {
            await _hot.PruneAsync(address, policy, cancellationToken).ConfigureAwait(false);
            LastCacheError = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LastCacheError = exception;
        }
    }

    public bool TryGetCapability(Type capabilityType, out object? capability)
    {
        ArgumentNullException.ThrowIfNull(capabilityType);

        if (capabilityType.IsInstanceOfType(_hot))
        {
            capability = _hot;
            return true;
        }

        if (_hot is IStateCapabilityProvider hotForwarder && hotForwarder.TryGetCapability(capabilityType, out capability))
        {
            return true;
        }

        if (capabilityType.IsInstanceOfType(_cold))
        {
            capability = _cold;
            return true;
        }

        if (_cold is IStateCapabilityProvider coldForwarder && coldForwarder.TryGetCapability(capabilityType, out capability))
        {
            return true;
        }

        capability = null;
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_ownsStores)
        {
            return;
        }

        await _hot.DisposeAsync().ConfigureAwait(false);
        if (!ReferenceEquals(_hot, _cold))
        {
            await _cold.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<StateRecord?> TryReadHotAsync(
        StateAddress address,
        CancellationToken cancellationToken)
    {
        try
        {
            StateRecord? record = await _hot.ReadLatestAsync(address, cancellationToken).ConfigureAwait(false);
            LastCacheError = null;
            return record;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LastCacheError = exception;
            return null;
        }
    }

    private async ValueTask TryImportAsync(StateRecord record, CancellationToken cancellationToken)
    {
        try
        {
            await _hotReplica.ImportAsync(record, cancellationToken).ConfigureAwait(false);
            LastCacheError = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The cold read or append is already authoritative. Cache maintenance cannot reverse it.
            LastCacheError = exception;
        }
    }
}

public static class TieredStatesmanExtensions
{
    public static StatesmanServiceBuilder UseTieredStore(
        this StatesmanServiceBuilder builder,
        string name,
        string hotStore,
        string coldStore,
        Action<TieredStateLedgerStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(hotStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(coldStore);
        if (string.Equals(name, hotStore, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, coldStore, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A tiered store cannot reference itself as its hot or cold store.", nameof(name));
        }

        if (string.Equals(hotStore, coldStore, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A tiered store requires distinct hot and cold stores.", nameof(coldStore));
        }

        var options = new TieredStateLedgerStoreOptions();
        configure?.Invoke(options);
        return builder.UseStore(name, services =>
        {
            var resolver = (IStateStoreResolver)(services.GetService(typeof(IStateStoreResolver))
                ?? throw new InvalidOperationException("Statesman store resolution is not registered."));
            return new TieredStateLedgerStore(
                name,
                resolver.Resolve(hotStore),
                resolver.Resolve(coldStore),
                options);
        });
    }
}
