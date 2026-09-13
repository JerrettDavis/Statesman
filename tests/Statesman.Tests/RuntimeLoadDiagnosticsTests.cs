using Statesman.Testing;

namespace Statesman.Tests;

/// <summary>
/// The public load-diagnostics surface: what a completed load reports about each declared source, how
/// the retained set is bounded, and how it is drained. Reached through
/// <see cref="StatesmanDiagnosticsExtensions.TryGetDiagnostics"/>, the same convention the
/// maintenance-failure surface uses, rather than through a new member on <see cref="IStatesman"/>.
/// </summary>
public sealed class RuntimeLoadDiagnosticsTests
{
    private static readonly StateKey<int> Timed = StateKey.Define<int>("load-diagnostics/timed");

    [Fact]
    public async Task The_surface_is_reachable_through_TryGetDiagnostics()
    {
        await using StatesmanTestHarness harness = CreateTwoSources();

        Assert.True(harness.Runtime.TryGetDiagnostics(out IStatesmanDiagnostics? diagnostics));
        Assert.Empty(diagnostics.ReadLoadDiagnostics().Reports);
        Assert.Equal(0L, diagnostics.ReadLoadDiagnostics().Completed);
    }

    [Fact]
    public async Task The_report_carries_each_source_in_declaration_order_with_its_exact_elapsed_time()
    {
        await using StatesmanTestHarness harness = CreateTwoSources();
        Assert.True(harness.Runtime.TryGetDiagnostics(out IStatesmanDiagnostics? diagnostics));

        _ = await harness.Runtime.State(Timed).RefreshAsync();

        StateLoadReport report = Assert.Single(diagnostics.ReadLoadDiagnostics().Reports);
        Assert.Equal("complete", report.Completeness);
        Assert.Equal(2, report.SourcesReady);
        Assert.Equal(0, report.SourcesFaulted);
        Assert.Equal(TimeSpan.FromMilliseconds(350), report.Elapsed);
        Assert.Equal(report.StartedAt + report.Elapsed, report.CompletedAt);
        Assert.Equal(new[] { "slow", "fast" }, report.Sources.Select(source => source.Name));
        Assert.Equal(TimeSpan.FromMilliseconds(250), report.Sources[0].Elapsed);
        Assert.Equal(TimeSpan.FromMilliseconds(100), report.Sources[1].Elapsed);
        Assert.All(report.Sources, source => Assert.Equal(StateSourceLoadStatus.Ready, source.Status));
    }

    [Fact]
    public async Task A_faulted_source_carries_its_own_exception_type_and_message()
    {
        // The source's own exception, not the InvalidOperationException the loader wraps it in: an
        // operator reading "State source 'broken' failed." learns nothing the inner type and message
        // do not say better.
        var clock = new ManualTimeProvider();
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("load-diagnostics-partial")
            .State(Timed, state => state
                .Initial(0)
                .Load(load => load
                    .From<TimeProvider, int>("ok", (_, context, _) =>
                    {
                        ((ManualTimeProvider)context.TimeProvider).Advance(TimeSpan.FromMilliseconds(250));
                        return ValueTask.FromResult(1);
                    })
                    .Into((_, value, _) => value)
                    .From<TimeProvider, int>("broken", (_, context, _) =>
                    {
                        ((ManualTimeProvider)context.TimeProvider).Advance(TimeSpan.FromMilliseconds(100));
                        throw new TimeoutException("upstream timed out");
                    })
                    .Into((current, _, _) => current)
                    .BestEffort()))
            .Build();
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(declaration, time: clock);
        Assert.True(harness.Runtime.TryGetDiagnostics(out IStatesmanDiagnostics? diagnostics));

        _ = await harness.Runtime.State(Timed).RefreshAsync();

        StateLoadReport report = Assert.Single(diagnostics.ReadLoadDiagnostics().Reports);
        Assert.Equal("partial", report.Completeness);
        Assert.Equal(1, report.SourcesReady);
        Assert.Equal(1, report.SourcesFaulted);
        StateSourceLoadReport broken = report.Sources.Single(source => source.Name == "broken");
        Assert.Equal(StateSourceLoadStatus.Faulted, broken.Status);
        Assert.Equal(TimeSpan.FromMilliseconds(100), broken.Elapsed);
        Assert.Equal(typeof(TimeoutException).FullName, broken.ExceptionType);
        Assert.Equal("upstream timed out", broken.ExceptionMessage);
    }

    [Fact]
    public async Task The_retained_report_is_the_latest_load_of_that_address()
    {
        await using StatesmanTestHarness harness = CreateTwoSources();
        Assert.True(harness.Runtime.TryGetDiagnostics(out IStatesmanDiagnostics? diagnostics));

        _ = await harness.Runtime.State(Timed).RefreshAsync();
        StateLoadReport first = Assert.Single(diagnostics.ReadLoadDiagnostics().Reports);
        _ = await harness.Runtime.State(Timed).RefreshAsync();

        LoadDiagnostics snapshot = diagnostics.ReadLoadDiagnostics();
        StateLoadReport second = Assert.Single(snapshot.Reports);
        Assert.Equal(2L, snapshot.Completed);
        Assert.Equal(0L, snapshot.Evicted);
        Assert.True(second.StartedAt > first.StartedAt);
    }

    [Fact]
    public async Task Retained_reports_are_bounded_and_the_oldest_completion_is_evicted()
    {
        // One address per partition, one more than the bound, loaded oldest first — so the evicted one
        // is the first partition and every later one survives.
        var clock = new ManualTimeProvider();
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("load-diagnostics-bound")
            .State(Timed, state => state
                .Partitioned()
                .Initial(0)
                .Load(load => load
                    .From<TimeProvider, int>("slow", (_, context, _) =>
                    {
                        ((ManualTimeProvider)context.TimeProvider).Advance(TimeSpan.FromMilliseconds(1));
                        return ValueTask.FromResult(1);
                    })
                    .Into((_, value, _) => value)))
            .Build();
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(declaration, time: clock);
        Assert.True(harness.Runtime.TryGetDiagnostics(out IStatesmanDiagnostics? diagnostics));

        for (int index = 0; index <= StatesmanDiagnostics.MaxRetainedLoadReports; index++)
        {
            _ = await harness.Runtime.State(Timed, $"p{index:D3}").RefreshAsync();
        }

        LoadDiagnostics snapshot = diagnostics.ReadLoadDiagnostics();
        Assert.Equal((long)StatesmanDiagnostics.MaxRetainedLoadReports + 1, snapshot.Completed);
        Assert.Equal(StatesmanDiagnostics.MaxRetainedLoadReports, snapshot.Reports.Count);
        Assert.Equal(1L, snapshot.Evicted);
        Assert.DoesNotContain(snapshot.Reports, report => report.Address.Partition.Value == "p000");
        Assert.Contains(snapshot.Reports, report => report.Address.Partition.Value == "p001");
    }

    [Fact]
    public async Task Clearing_drains_the_reports_and_leaves_the_lifetime_counts()
    {
        await using StatesmanTestHarness harness = CreateTwoSources();
        Assert.True(harness.Runtime.TryGetDiagnostics(out IStatesmanDiagnostics? diagnostics));
        _ = await harness.Runtime.State(Timed).RefreshAsync();

        Assert.Equal(1, diagnostics.ClearLoadDiagnostics());

        LoadDiagnostics snapshot = diagnostics.ReadLoadDiagnostics();
        Assert.Empty(snapshot.Reports);
        Assert.Equal(1L, snapshot.Completed);
        Assert.Equal(0, diagnostics.ClearLoadDiagnostics());
    }

    private static StatesmanTestHarness CreateTwoSources()
    {
        var clock = new ManualTimeProvider();
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("load-diagnostics")
            .State(Timed, state => state
                .Initial(0)
                .Load(load => load
                    .From<TimeProvider, int>("slow", (_, context, _) =>
                    {
                        ((ManualTimeProvider)context.TimeProvider).Advance(TimeSpan.FromMilliseconds(250));
                        return ValueTask.FromResult(1);
                    })
                    .Into((_, value, _) => value)
                    .From<TimeProvider, int>("fast", (_, context, _) =>
                    {
                        ((ManualTimeProvider)context.TimeProvider).Advance(TimeSpan.FromMilliseconds(100));
                        return ValueTask.FromResult(2);
                    })
                    .Into((_, value, _) => value)))
            .Build();
        return StatesmanTestHarness.Create(declaration, time: clock);
    }
}
