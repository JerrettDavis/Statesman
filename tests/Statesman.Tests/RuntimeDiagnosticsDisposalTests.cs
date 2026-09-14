using Statesman.Testing;

namespace Statesman.Tests;

/// <summary>
/// All four <see cref="IStatesmanDiagnostics"/> members answer after the runtime is disposed, and none
/// of them throws. This is deliberate rather than an oversight, and it is pinned rather than merely
/// recorded: a post-dispose read returns a stale-but-valid snapshot and mutates nothing, and
/// <c>StatesmanHealthCheck</c> calls two of these on every registered runtime with no disposal check of
/// its own — so a throwing surface would turn a health probe over a registry still holding a disposed
/// root into an exception rather than a report. If a later phase wants throwing behaviour it must
/// change all four together, which is a breaking behaviour change on a shipped surface.
/// ROADMAP 0.3 pre-Phase-17 addendum decision 69 as amended, and pre-Phase-18 decision 86.
/// </summary>
public sealed class RuntimeDiagnosticsDisposalTests
{
    private static readonly StateKey<int> Counter = StateKey.Define<int>("diagnostics-disposal/counter");

    [Fact]
    public async Task All_four_members_still_answer_after_the_root_is_disposed()
    {
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("diagnostics-disposal")
            .State(Counter, state => state.Initial(0))
            .Build();
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(declaration);
        Assert.True(harness.Runtime.TryGetDiagnostics(out IStatesmanDiagnostics? diagnostics));
        await harness.Runtime.State(Counter).SetAsync(1);

        // A state handle DOES throw after disposal, which is what makes this contrast deliberate
        // rather than accidental -- the diagnostics surface is the exception, not the rule.
        IState<int> state = harness.Runtime.State(Counter);
        await harness.Runtime.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => _ = state.Current);

        MaintenanceFailureDiagnostics failures = diagnostics.ReadMaintenanceFailures();
        Assert.NotNull(failures);
        Assert.Empty(failures.Retained);

        LoadDiagnostics loads = diagnostics.ReadLoadDiagnostics();
        Assert.NotNull(loads);
        Assert.Empty(loads.Reports);

        Assert.Equal(0, diagnostics.ClearMaintenanceFailures());
        Assert.Equal(0, diagnostics.ClearLoadDiagnostics());
    }

    [Fact]
    public async Task A_post_disposal_read_returns_the_snapshot_the_live_runtime_had()
    {
        // "Stale but valid" is the promise, so it is measured: the same numbers before and after.
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("diagnostics-disposal-stale")
            .State(Counter, state => state.Initial(0))
            .Build();
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(declaration);
        Assert.True(harness.Runtime.TryGetDiagnostics(out IStatesmanDiagnostics? diagnostics));
        _ = await harness.Runtime.State(Counter).RefreshAsync();

        LoadDiagnostics before = diagnostics.ReadLoadDiagnostics();
        await harness.Runtime.DisposeAsync();
        LoadDiagnostics after = diagnostics.ReadLoadDiagnostics();

        Assert.Equal(before.Completed, after.Completed);
        Assert.Equal(before.Evicted, after.Evicted);
        Assert.Equal(before.Reports.Count, after.Reports.Count);
    }
}
