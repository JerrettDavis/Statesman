namespace Statesman.Outbox.Tests;

/// <summary>
/// The behaviour every <see cref="IOutboxCursorStore"/> must have. Subclasses return a fresh handle
/// over the same backing store from <see cref="CreateStore"/>, so durability across handles is part
/// of the suite rather than a per-implementation extra.
/// </summary>
public abstract class OutboxCursorStoreConformanceTests
{
    /// <summary>A fresh handle over the same backing store. Called more than once per test.</summary>
    protected abstract IOutboxCursorStore CreateStore();

    /// <summary>
    /// Called first by every test in this suite. The base does nothing; an implementation backed by
    /// live infrastructure overrides it with <c>Assert.SkipUnless(...)</c> so the whole suite skips
    /// cleanly when that infrastructure is absent.
    /// </summary>
    protected virtual void SkipIfUnavailable()
    {
    }

    [Fact]
    public async Task A_read_before_any_write_returns_null()
    {
        SkipIfUnavailable();
        IOutboxCursorStore store = CreateStore();

        StateChangeCursor? cursor = await store.ReadAsync($"never-written-{Guid.NewGuid():N}");

        Assert.Null(cursor);
    }

    [Fact]
    public async Task A_written_cursor_is_read_back_by_a_fresh_handle()
    {
        SkipIfUnavailable();
        string outboxId = $"outbox-{Guid.NewGuid():N}";
        await CreateStore().WriteAsync(outboxId, new StateChangeCursor(42));

        StateChangeCursor? cursor = await CreateStore().ReadAsync(outboxId);

        Assert.Equal(42, cursor!.Value.Position);
    }

    [Fact]
    public async Task A_write_below_the_stored_value_leaves_it_unchanged()
    {
        SkipIfUnavailable();
        string outboxId = $"outbox-{Guid.NewGuid():N}";
        IOutboxCursorStore store = CreateStore();
        await store.WriteAsync(outboxId, new StateChangeCursor(100));

        await store.WriteAsync(outboxId, new StateChangeCursor(50));

        StateChangeCursor? cursor = await CreateStore().ReadAsync(outboxId);
        Assert.Equal(100, cursor!.Value.Position);
    }

    [Fact]
    public async Task A_write_equal_to_the_stored_value_leaves_it_unchanged()
    {
        SkipIfUnavailable();
        string outboxId = $"outbox-{Guid.NewGuid():N}";
        IOutboxCursorStore store = CreateStore();
        await store.WriteAsync(outboxId, new StateChangeCursor(100));

        await store.WriteAsync(outboxId, new StateChangeCursor(100));

        StateChangeCursor? cursor = await CreateStore().ReadAsync(outboxId);
        Assert.Equal(100, cursor!.Value.Position);
    }

    [Fact]
    public async Task A_position_above_two_to_the_fifty_third_round_trips_exactly()
    {
        SkipIfUnavailable();
        const long Position = 638000000000000000;
        string outboxId = $"outbox-{Guid.NewGuid():N}";
        await CreateStore().WriteAsync(outboxId, new StateChangeCursor(Position));

        StateChangeCursor? cursor = await CreateStore().ReadAsync(outboxId);

        Assert.Equal(Position, cursor!.Value.Position);
    }

    [Fact]
    public async Task Cursors_are_isolated_per_outbox_id()
    {
        SkipIfUnavailable();
        string first = $"outbox-{Guid.NewGuid():N}";
        string second = $"outbox-{Guid.NewGuid():N}";
        IOutboxCursorStore store = CreateStore();

        await store.WriteAsync(first, new StateChangeCursor(10));
        await store.WriteAsync(second, new StateChangeCursor(20));

        Assert.Equal(10, (await store.ReadAsync(first))!.Value.Position);
        Assert.Equal(20, (await store.ReadAsync(second))!.Value.Position);
    }

    [Fact]
    public async Task Concurrent_writers_converge_on_the_maximum()
    {
        SkipIfUnavailable();
        string outboxId = $"outbox-{Guid.NewGuid():N}";
        IOutboxCursorStore store = CreateStore();

        await Task.WhenAll(Enumerable.Range(1, 64).Select(async position =>
            await store.WriteAsync(outboxId, new StateChangeCursor(position))));

        StateChangeCursor? cursor = await CreateStore().ReadAsync(outboxId);
        Assert.Equal(64, cursor!.Value.Position);
    }
}

public sealed class InMemoryOutboxCursorStoreTests : OutboxCursorStoreConformanceTests
{
    private readonly InMemoryOutboxCursorStore _store = new();

    protected override IOutboxCursorStore CreateStore() => _store;
}

public sealed class FileSystemOutboxCursorStoreTests : OutboxCursorStoreConformanceTests, IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "statesman-outbox-cursor-" + Guid.NewGuid().ToString("N"));

    protected override IOutboxCursorStore CreateStore() => new FileSystemOutboxCursorStore(_directory);

    [Fact]
    public async Task A_cursor_file_is_written_per_outbox_id()
    {
        await CreateStore().WriteAsync("alpha", new StateChangeCursor(7));
        await CreateStore().WriteAsync("beta", new StateChangeCursor(9));

        Assert.Equal(2, Directory.GetFiles(_directory, "*.cursor").Length);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
