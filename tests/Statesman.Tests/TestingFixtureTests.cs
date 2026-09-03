using Statesman.Testing;

namespace Statesman.Tests;

public sealed class TestingFixtureTests
{
    [Fact]
    public async Task Fluent_seed_creates_repeatable_partitioned_state()
    {
        StateKey<UserState> user = StateKey.Define<UserState>("users/user");
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("testing")
            .State(user, state => state.Partitioned())
            .Build();

        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(declaration);
        await harness.Seed()
            .Source("scenario:admin-user")
            .Metadata("case", "admin-can-edit")
            .State(user, new UserState("JD", true), new StatePartition("jd"))
            .ApplyAsync();

        IStateSnapshot<UserState> snapshot = await harness.Runtime
            .State(user, new StatePartition("jd"))
            .GetAsync(StateReadOptions.Cached);

        snapshot.ShouldBeReady().ShouldEqualValue(new UserState("JD", true));
        Assert.Equal("true", snapshot.Metadata["statesman.seed"]);
        Assert.Equal("admin-can-edit", snapshot.Metadata["case"]);
    }

    [Fact]
    public async Task Captured_fixture_can_seed_a_fresh_runtime()
    {
        StateKey<UserState> user = StateKey.Define<UserState>("users/current");
        StateKey<FeatureState> features = StateKey.Define<FeatureState>("features/current");
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("testing")
            .State(user, _ => { })
            .State(features, _ => { })
            .Build();

        StateFixture fixture;
        await using (StatesmanTestHarness source = StatesmanTestHarness.Create(declaration))
        {
            await source.Seed()
                .State(user, new UserState("JD", true))
                .State(features, new FeatureState(true, false))
                .ApplyAsync();

            fixture = await source.Runtime.CaptureFixtureAsync(new[]
            {
                new StateReference(user.Path),
                new StateReference(features.Path),
            });
        }

        string json = fixture.ToJson();
        StateFixture portable = StateFixtureExtensions.FromJson(json);

        await using StatesmanTestHarness target = StatesmanTestHarness.Create(declaration);
        await target.ApplyFixtureAsync(portable);

        target.Runtime.State(user).Current.ShouldEqualValue(new UserState("JD", true));
        target.Runtime.State(features).Current.ShouldEqualValue(new FeatureState(true, false));
    }

    private sealed record UserState(string Name, bool IsAdmin);

    private sealed record FeatureState(bool NewCheckout, bool ExperimentalSearch);
}
