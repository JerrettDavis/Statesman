using Statesman.Testing;

namespace Statesman.Tests;

public sealed class LoaderTests
{
    private static readonly StateKey<UserState> User = StateKey.Define<UserState>("users/user");

    [Fact]
    public async Task Parallel_facets_are_projected_in_declaration_order()
    {
        var api = new FakeUserApi();
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("loaders")
            .State(User, state => state
                .Partitioned()
                .Initial(new UserState(string.Empty, Array.Empty<string>(), 0))
                .Freshness(freshness => freshness.FreshFor(TimeSpan.FromMinutes(1)))
                .Refresh(refresh => refresh.OnFirstRead().WhenStale().OnSignal("user.changed"))
                .Load(load => load
                    .From<IUserApi, UserProfile>("profile", (service, context, cancellationToken) =>
                        service.GetProfileAsync(context.Address.Partition.Value, cancellationToken))
                    .Into((current, profile, _) => current with { Name = profile.Name, ProjectionOrder = 1 })
                    .From<IUserApi, IReadOnlyList<string>>("roles", (service, context, cancellationToken) =>
                        service.GetRolesAsync(context.Address.Partition.Value, cancellationToken))
                    .Into((current, roles, _) => current with { Roles = roles, ProjectionOrder = current.ProjectionOrder + 1 })
                    .InParallel()
                    .RequireAll()))
            .Build();
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(
            declaration,
            services => services.Add<IUserApi>(api));
        IState<UserState> state = harness.Runtime.State(User, "42");

        IStateSnapshot<UserState> loaded = await state.GetAsync();
        Assert.Equal("User 42", loaded.RequiredValue.Name);
        Assert.Equal(new[] { "reader", "operator" }, loaded.RequiredValue.Roles);
        Assert.Equal(2, loaded.RequiredValue.ProjectionOrder);

        harness.Time.Advance(TimeSpan.FromMinutes(2));
        await harness.Runtime.SignalAsync(new StateSignal("user.changed", "42"));
        Assert.Equal(2, api.ProfileCalls);
    }

    [Fact]
    public async Task Best_effort_loading_keeps_successful_facets()
    {
        var api = new FakeUserApi { FailRoles = true };
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("best-effort")
            .State(User, state => state
                .Initial(new UserState(string.Empty, Array.Empty<string>(), 0))
                .Load(load => load
                    .From<IUserApi, UserProfile>("profile", (service, _, cancellationToken) =>
                        service.GetProfileAsync("default", cancellationToken))
                    .Into((current, profile, _) => current with { Name = profile.Name })
                    .From<IUserApi, IReadOnlyList<string>>("roles", (service, _, cancellationToken) =>
                        service.GetRolesAsync("default", cancellationToken))
                    .Into((current, roles, _) => current with { Roles = roles })
                    .InParallel()
                    .BestEffort()))
            .Build();
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(
            declaration,
            services => services.Add<IUserApi>(api));

        IStateSnapshot<UserState> result = await harness.Runtime.State(User).RefreshAsync();

        Assert.Equal(StateStatus.Ready, result.Status);
        Assert.Equal("User default", result.RequiredValue.Name);
        Assert.Empty(result.RequiredValue.Roles);
        Assert.Equal("partial-load", result.Error?.Code);
        Assert.Equal("partial", result.Metadata["statesman.load.completeness"]);
        Assert.Equal("faulted", result.Metadata["statesman.source.roles.status"]);
    }

    private interface IUserApi
    {
        ValueTask<UserProfile> GetProfileAsync(string id, CancellationToken cancellationToken);

        ValueTask<IReadOnlyList<string>> GetRolesAsync(string id, CancellationToken cancellationToken);
    }

    private sealed class FakeUserApi : IUserApi
    {
        public int ProfileCalls { get; private set; }

        public bool FailRoles { get; init; }

        public ValueTask<UserProfile> GetProfileAsync(string id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProfileCalls++;
            return ValueTask.FromResult(new UserProfile(id, $"User {id}"));
        }

        public ValueTask<IReadOnlyList<string>> GetRolesAsync(string id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailRoles)
            {
                throw new HttpRequestException("Roles are unavailable.");
            }

            return ValueTask.FromResult<IReadOnlyList<string>>(new[] { "reader", "operator" });
        }
    }

    private sealed record UserProfile(string Id, string Name);

    [ManagedState]
    private sealed record UserState(string Name, IReadOnlyList<string> Roles, int ProjectionOrder);
}
