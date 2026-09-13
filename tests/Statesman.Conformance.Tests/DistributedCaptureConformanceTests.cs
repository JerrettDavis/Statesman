namespace Statesman.Conformance.Tests;

/// <summary>
/// The multi-address capture contract every <see cref="IDistributedCapture"/> implementation shares,
/// run once per provider by a subclass. Capability presence is answered by
/// <see cref="StateCapabilityExtensions.TryGetCapability{TCapability}"/> at the test site, never by a
/// per-provider boolean: per <c>docs/architecture/capabilities.md</c> the in-memory and filesystem
/// providers have no distributed capture at all, and skip every fact here with an honest "No" rather
/// than a silent pass.
/// </summary>
public abstract class DistributedCaptureConformanceTests
{
    /// <summary>
    /// Creates a store with <b>default</b> options, or returns <see langword="null"/> when this
    /// provider's infrastructure is not available here.
    /// </summary>
    protected abstract ValueTask<ConformanceStore?> CreateAsync();

    /// <summary>Why this provider was skipped, shown when <see cref="CreateAsync"/> returns null.</summary>
    protected virtual string SkipReason => "This provider's infrastructure is not available.";

    /// <summary>
    /// Asserts that a capture returns an entry — possibly <see langword="null"/> — for every
    /// requested address, including one never written. Public and static so the negative test in
    /// this project can run it against a double that omits absent addresses from its result, and
    /// require it to fail.
    /// </summary>
    /// <param name="store">The store to write through.</param>
    /// <param name="capture">The capture capability under test.</param>
    public static async Task AssertEveryRequestedAddressGetsAnEntryAsync(IStateLedgerStore store, IDistributedCapture capture)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(capture);
        var addressA = new StateAddress("app", $"conformance/capture-a-{Guid.NewGuid():N}", StatePartition.Default);
        var addressB = new StateAddress("app", $"conformance/capture-b-{Guid.NewGuid():N}", StatePartition.Default);
        var addressAbsent = new StateAddress("app", $"conformance/capture-absent-{Guid.NewGuid():N}", StatePartition.Default);

        StateAppendResult a = await store.AppendAsync(addressA, StateWriteCondition.Absent, Commit("a1"));
        StateAppendResult b = await store.AppendAsync(addressB, StateWriteCondition.Absent, Commit("b1"));

        IReadOnlyDictionary<StateAddress, StateRecord?> captured = await capture.CaptureAsync(
            new[] { addressA, addressB, addressAbsent }, StateCaptureConsistency.ReadCommittedDistributed);

        Assert.Equal(3, captured.Count);
        Assert.True(captured.ContainsKey(addressA), "The capture result is missing a written address.");
        Assert.Equal(a.Record!.GlobalPosition, captured[addressA]!.GlobalPosition);
        Assert.True(captured.ContainsKey(addressB), "The capture result is missing a written address.");
        Assert.Equal(b.Record!.GlobalPosition, captured[addressB]!.GlobalPosition);
        Assert.True(captured.ContainsKey(addressAbsent), "The caller must never need to handle a missing key.");
        Assert.Null(captured[addressAbsent]);
    }

    [Fact]
    public async Task CaptureAsync_returns_an_empty_result_for_an_empty_address_set()
    {
        // Per Ledger.cs: "An empty address collection returns an empty dictionary without contacting
        // the store at all."
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        Assert.SkipUnless(
            store!.Store.TryGetCapability(out IDistributedCapture? capture), "This provider has no distributed capture.");

        IReadOnlyDictionary<StateAddress, StateRecord?> captured = await capture!.CaptureAsync(
            Array.Empty<StateAddress>(), StateCaptureConsistency.ReadCommittedDistributed);

        Assert.Empty(captured);
    }

    [Fact]
    public async Task CaptureAsync_returns_an_entry_for_every_requested_address_including_absent_ones()
    {
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        Assert.SkipUnless(
            store!.Store.TryGetCapability(out IDistributedCapture? capture), "This provider has no distributed capture.");

        await AssertEveryRequestedAddressGetsAnEntryAsync(store.Store, capture!);
    }

    [Fact]
    public async Task CaptureAsync_rejects_an_out_of_domain_consistency_level()
    {
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        Assert.SkipUnless(
            store!.Store.TryGetCapability(out IDistributedCapture? capture), "This provider has no distributed capture.");

        var address = new StateAddress("app", "conformance/capture-out-of-domain", StatePartition.Default);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await capture!.CaptureAsync(new[] { address }, (StateCaptureConsistency)99));
    }

    private static StateCommit Commit(string value) => new()
    {
        Operation = StateOperation.Set,
        Status = StateStatus.Ready,
        ValueType = typeof(string).FullName!,
        SchemaVersion = 1,
        Payload = System.Text.Encoding.UTF8.GetBytes(value),
        Source = "test",
    };
}
