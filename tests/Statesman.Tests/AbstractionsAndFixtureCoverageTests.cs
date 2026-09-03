using System.Reflection;
using System.Text;
using System.Text.Json;
using Statesman.Testing;

namespace Statesman.Tests;

public sealed class AbstractionsAndFixtureCoverageTests
{
    private static readonly StateKey<UserState> User = StateKey.Define<UserState>("users/current");
    private static readonly StateKey<FeatureState> Features = StateKey.Define<FeatureState>("features/current");

    [Fact]
    public void Identity_and_option_primitives_normalize_validate_and_format()
    {
        StatePath path = new(" //Users\\\\Profile// ");
        StatePath child = path.Child("Settings");
        Assert.Equal("users/profile", path.Value);
        Assert.Equal("users/profile/settings", child.Value);
        Assert.True(child.IsDescendantOf(path));
        Assert.Equal(0, path.CompareTo("users/profile"));
        Assert.Equal("users/profile", path.ToString());
        Assert.Equal("users/profile", (string)path);

        Assert.True(StateKey.TryDefine<int>("counts/active", out StateKey<int> countKey));
        Assert.False(StateKey.TryDefine<int>(" ", out _));
        Assert.Equal("counts/active<Int32>", countKey.ToString());
        Assert.Equal(new StateReference(countKey.Path, new StatePartition("tenant-1")), countKey.At("tenant-1"));

        StatePartition partition = new(" Tenant-1 ");
        Assert.Equal("Tenant-1", partition.Value);
        Assert.Throws<ArgumentException>(() => new StatePartition(" "));
        Assert.Throws<ArgumentException>(() => new StatePartition("bad\r\npartition"));
        Assert.Throws<ArgumentException>(() => new StatePath("bad\u0001path"));

        var address = new StateAddress(" Root ", path, partition);
        address.Validate();
        Assert.Equal("root::users/profile::Tenant-1", address.Canonical);

        Assert.Throws<InvalidOperationException>(() =>
            new StateFreshnessPolicy { FreshFor = TimeSpan.FromMinutes(-1) }.Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new StateFreshnessPolicy { ServeStaleFor = TimeSpan.FromMinutes(-1) }.Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new StateRetentionPolicy { MaxRevisions = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new StateRetentionPolicy { MaxAge = TimeSpan.Zero }.Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new StateRetentionPolicy { MaxBytes = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new StateRefreshPolicy { Interval = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StateHistoryOptions { Take = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StateHistoryOptions { BeforeRevision = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StateObservationOptions { BufferCapacity = 0 }.Validate());

        DateTimeOffset now = DateTimeOffset.UtcNow;
        StateSnapshot<int> fresh = new()
        {
            Address = new StateAddress("app", "counts/active", StatePartition.Default),
            ObservedAt = now,
            HasValue = true,
            Value = 5,
            Status = StateStatus.Ready,
            FreshUntil = now.AddMinutes(1),
        };
        StateSnapshot<int> stale = fresh with { FreshUntil = now.AddMinutes(-1) };
        StateSnapshot<int> absent = StateSnapshot<int>.Absent(new StateAddress("app", "counts/active", StatePartition.Default), now);

        Assert.True(fresh.IsFresh);
        Assert.False(fresh.IsStale);
        Assert.True(stale.IsStale);
        Assert.Equal(5, fresh.RequiredValue);
        Assert.Throws<StateUnavailableException>(() => _ = absent.RequiredValue);

        var unavailable = new StateUnavailableException(address, StateStatus.Faulted, new StateError("fault", "bad"));
        var concurrent = new StateConcurrencyException(address, 1, 2);
        var rejected = new StateInteractionRejectedException(address, "rename", new[] { "Denied" });
        var invariant = new StateInvariantException(path, "broken");

        Assert.Equal(address, unavailable.Address);
        Assert.Equal(1, concurrent.Expected);
        Assert.Equal("rename", rejected.Interaction);
        Assert.Equal(path, invariant.Path);
        Assert.Throws<ArgumentException>(() => StatePath.Root.Child("users/profile"));
        Assert.Throws<ArgumentException>(() => new StateAddress("", path, partition).Validate());
        Assert.Throws<ArgumentException>(() => new StateAddress("root", StatePath.Root, partition).Validate());
    }

    [Fact]
    public void Ledger_primitives_validate_success_and_failure_paths()
    {
        var address = new StateAddress("app", "users/current", StatePartition.Default);
        byte[] payload = Encoding.UTF8.GetBytes("{}");

        var record = new StateRecord
        {
            Address = address,
            Revision = 1,
            GlobalPosition = 2,
            OccurredAt = DateTimeOffset.UtcNow,
            Operation = StateOperation.Set,
            Status = StateStatus.Ready,
            ValueType = typeof(UserState).FullName!,
            SchemaVersion = 1,
            Payload = payload,
            FreshUntil = DateTimeOffset.UtcNow,
            ServeUntil = DateTimeOffset.UtcNow.AddMinutes(1),
            Source = "test",
        };
        record.Validate();
        Assert.Equal(payload.LongLength, record.PayloadLength);

        var commit = new StateCommit
        {
            Operation = StateOperation.Set,
            Status = StateStatus.Ready,
            ValueType = typeof(UserState).FullName!,
            Payload = payload,
            FreshUntil = DateTimeOffset.UtcNow,
            ServeUntil = DateTimeOffset.UtcNow.AddMinutes(1),
            Source = "test",
        };
        commit.Validate();

        Assert.Throws<ArgumentException>(() => (record with { Status = StateStatus.Absent }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (record with { Revision = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (record with { GlobalPosition = 0 }).Validate());
        Assert.Throws<ArgumentException>(() => (record with { OccurredAt = default }).Validate());
        Assert.Throws<ArgumentException>(() => (record with { Payload = null }).Validate());
        Assert.Throws<ArgumentException>(() => (record with { Operation = StateOperation.Cleared, Payload = payload }).Validate());
        Assert.Throws<ArgumentException>(() => (record with { ValueType = " " }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (record with { SchemaVersion = 0 }).Validate());
        Assert.Throws<ArgumentException>(() => (record with { Source = " " }).Validate());
        Assert.Throws<ArgumentException>(() => (record with { FreshUntil = DateTimeOffset.UtcNow, ServeUntil = DateTimeOffset.UtcNow.AddMinutes(-1) }).Validate());
        Assert.Throws<ArgumentException>(() => (commit with { Status = StateStatus.Absent }).Validate());
        Assert.Throws<ArgumentException>(() => (commit with { Payload = null }).Validate());
        Assert.Throws<ArgumentException>(() => (commit with { Operation = StateOperation.Cleared, Payload = payload }).Validate());
        Assert.Throws<ArgumentException>(() => (commit with { ValueType = " " }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (commit with { SchemaVersion = 0 }).Validate());
        Assert.Throws<ArgumentException>(() => (commit with { Source = " " }).Validate());
        Assert.Throws<ArgumentException>(() => (commit with { FreshUntil = DateTimeOffset.UtcNow, ServeUntil = DateTimeOffset.UtcNow.AddMinutes(-1) }).Validate());

        StateWriteCondition any = StateWriteCondition.Any;
        StateWriteCondition absent = StateWriteCondition.Absent;
        StateWriteCondition atRevision = StateWriteCondition.AtRevision(3);
        any.Validate();
        absent.Validate();
        atRevision.Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => StateWriteCondition.AtRevision(0));
        Assert.Throws<ArgumentException>(() => new StateWriteCondition(3, true).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new StateWriteCondition(0, false).Validate());

        StateAppendResult appended = StateAppendResult.Appended(record);
        StateAppendResult conflict = StateAppendResult.Conflict(record);
        Assert.True(appended.Succeeded);
        Assert.Equal(record, appended.Record);
        Assert.False(conflict.Succeeded);
        Assert.Equal(record, conflict.Current);
    }

    [Fact]
    public async Task Fixture_application_handles_mismatches_unknown_states_and_non_ready_statuses()
    {
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("fixtures")
            .State(User, _ => { })
            .State(Features, _ => { })
            .Build();

        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(declaration);
        var goodFixture = new StateFixture
        {
            Root = "fixtures",
            ManifestFingerprint = declaration.Manifest.Fingerprint,
            CapturedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["suite"] = "fixture-tests",
            },
            States =
            [
                new StateFixtureEntry
                {
                    Path = User.Path.Value,
                    Partition = "default",
                    ValueType = typeof(UserState).FullName!,
                    AssemblyQualifiedValueType = typeof(UserState).AssemblyQualifiedName,
                    Status = StateStatus.Invalidated,
                    HasValue = true,
                    ValueJson = JsonSerializer.Serialize(new UserState("JD", true)),
                    Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["case"] = "invalidated",
                    },
                },
                new StateFixtureEntry
                {
                    Path = Features.Path.Value,
                    Partition = "default",
                    ValueType = typeof(FeatureState).FullName!,
                    AssemblyQualifiedValueType = typeof(FeatureState).AssemblyQualifiedName,
                    Status = StateStatus.Cleared,
                    HasValue = false,
                },
                new StateFixtureEntry
                {
                    Path = "legacy/unknown",
                    Partition = "default",
                    ValueType = typeof(UserState).FullName!,
                    AssemblyQualifiedValueType = typeof(UserState).AssemblyQualifiedName,
                    Status = StateStatus.Ready,
                    HasValue = true,
                    ValueJson = JsonSerializer.Serialize(new UserState("Legacy", false)),
                },
            ],
        };

        await Assert.ThrowsAsync<StateFixtureException>(async () =>
            await harness.ApplyFixtureAsync(goodFixture with { Root = "other-root" }));
        await Assert.ThrowsAsync<StateFixtureException>(async () =>
            await harness.ApplyFixtureAsync(goodFixture with { ManifestFingerprint = "different" }));
        await Assert.ThrowsAsync<StateFixtureException>(async () =>
            await harness.ApplyFixtureAsync(goodFixture));

        await harness.ApplyFixtureAsync(goodFixture, new StateFixtureApplyOptions
        {
            FailOnUnknownState = false,
        });

        IStateSnapshot<UserState> invalidated = await harness.Runtime.State(User).GetAsync(StateReadOptions.Cached);
        IStateSnapshot<FeatureState> cleared = await harness.Runtime.State(Features).GetAsync(StateReadOptions.Cached);

        Assert.Equal(StateStatus.Invalidated, invalidated.Status);
        Assert.Equal("fixture-tests", invalidated.Metadata["suite"]);
        Assert.Equal("invalidated", invalidated.Metadata["case"]);
        Assert.Equal("true", invalidated.Metadata["statesman.fixture"]);
        Assert.Equal(StateStatus.Cleared, cleared.Status);
    }

    [Fact]
    public async Task Fixture_capture_serialization_persistence_and_test_harness_helpers_work()
    {
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("testing")
            .State(User, _ => { })
            .Build();

        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(declaration);
        await harness.Seed()
            .Source("scenario")
            .Metadata("case", "helpers")
            .State(User, new UserState("JD", true))
            .ApplyAsync();

        IReadOnlyList<StateChange<UserState>> changes = await harness.CollectChangesAsync(
            harness.Runtime.State(User),
            async () =>
            {
                await Task.Delay(50);
                await harness.Runtime.State(User).SetAsync(new UserState("Ada", false));
            },
            expected: 1);
        Assert.Single(changes);

        StateFixture fixture = await harness.Runtime.CaptureFixtureAsync(new[] { new StateReference(User.Path) });
        string json = fixture.ToJson();
        StateFixture roundTrip = StateFixtureExtensions.FromJson(json);

        string path = Path.GetTempFileName();
        try
        {
            await roundTrip.SaveAsync(path);
            StateFixture reloaded = await StateFixtureExtensions.LoadFixtureAsync(path);
            Assert.Equal(roundTrip.Root, reloaded.Root);
        }
        finally
        {
            File.Delete(path);
        }

        await StatesmanTestHarness.EventuallyAsync(() => true, TimeSpan.FromMilliseconds(1));
        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await StatesmanTestHarness.EventuallyAsync(() => false, TimeSpan.FromMilliseconds(20)));
        Assert.Throws<StateFixtureException>(() => StateFixtureExtensions.FromJson("null"));
    }

    [Fact]
    public async Task Seed_builder_assertion_helpers_and_attributes_cover_remaining_testing_paths()
    {
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("seed-testing")
            .State(User, _ => { })
            .State(Features, _ => { })
            .Build();

        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(declaration);
        Assert.Throws<ArgumentException>(() => harness.Seed().Source(" "));
        Assert.Throws<ArgumentException>(() => harness.Seed().Metadata(" ", "value"));
        Assert.Throws<ArgumentNullException>(() => harness.Seed().Metadata("case", null!));

        await harness.Seed()
            .Source("seed-scenario")
            .Metadata("case", "seed")
            .Invalidated(User, new UserState("Seeded", false), "stale")
            .Cleared(Features)
            .ApplyAsync();

        IStateSnapshot<UserState> invalidated = await harness.Runtime.State(User).GetAsync(StateReadOptions.Cached);
        IStateSnapshot<FeatureState> cleared = await harness.Runtime.State(Features).GetAsync(StateReadOptions.Cached);

        invalidated.ShouldHaveRevision(2);
        Assert.Equal(StateStatus.Invalidated, invalidated.Status);
        Assert.Equal(StateOperation.Invalidated, invalidated.Operation);
        Assert.Equal("seed", invalidated.Metadata["case"]);
        Assert.Equal("true", invalidated.Metadata["statesman.seed"]);
        Assert.Equal(StateStatus.Cleared, cleared.Status);
        Assert.Equal("true", cleared.Metadata["statesman.seed"]);

        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var ready = new StateSnapshot<UserState>
        {
            Address = new StateAddress("testing", User.Path, StatePartition.Default),
            Revision = 3,
            ObservedAt = now,
            Status = StateStatus.Ready,
            HasValue = true,
            Value = new UserState("JD", true),
        };

        Assert.Same(ready, ready.ShouldBeReady());
        Assert.Same(ready, ready.ShouldHaveRevision(3));
        Assert.Same(ready, ready.ShouldEqualValue(new UserState("JD", true)));
        Assert.Throws<StateAssertionException>(() => ready.ShouldHaveRevision(4));
        Assert.Throws<StateAssertionException>(() => ready.ShouldEqualValue(new UserState("Ada", false)));
        Assert.Throws<StateAssertionException>(() => (ready with { Status = StateStatus.Absent, HasValue = false, Value = null }).ShouldBeReady());

        var clock = new ManualTimeProvider(now);
        Assert.Equal(now, clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(now.AddMinutes(5), clock.GetUtcNow());
        clock.SetUtcNow(new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.FromHours(2)));
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 6, 0, 0, TimeSpan.Zero), clock.GetUtcNow());
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(TimeSpan.FromMinutes(-1)));

        var mismatch = new StateTypeMismatchException(User.Path, typeof(UserState), typeof(FeatureState));
        Assert.Contains(User.Path.Value, mismatch.Message);
        Assert.Contains(typeof(UserState).FullName!, mismatch.Message);
        Assert.Contains(typeof(FeatureState).FullName!, mismatch.Message);

        ManagedStateAttribute? managed = typeof(ManagedUserState).GetCustomAttribute<ManagedStateAttribute>();
        StateMutationBoundaryAttribute? boundary = typeof(MutationBoundaryAdapter).GetCustomAttribute<StateMutationBoundaryAttribute>();
        StateMutationAnalysisIgnoreAttribute? ignored = typeof(IgnoredMutationAdapter).GetCustomAttribute<StateMutationAnalysisIgnoreAttribute>();

        Assert.NotNull(managed);
        Assert.Equal("migration", boundary?.Reason);
        Assert.Equal("intentional escape hatch", ignored?.Reason);
    }

    private sealed record UserState(string Name, bool IsAdmin);

    private sealed record FeatureState(bool Enabled, bool Preview);

    [ManagedState]
    private sealed record ManagedUserState(string Name);

    [StateMutationBoundary("migration")]
    private sealed class MutationBoundaryAdapter;

    [StateMutationAnalysisIgnore("intentional escape hatch")]
    private sealed class IgnoredMutationAdapter;
}
