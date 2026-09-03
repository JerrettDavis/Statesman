using Statesman.Testing;

namespace Statesman.Tests;

public sealed class BuilderAndManifestCoverageTests
{
    private static readonly StateKey<AccountState> Account = StateKey.Define<AccountState>("users/account");
    private static readonly StateKey<string> Theme = StateKey.Define<string>("users/settings/theme");

    [Fact]
    public async Task Builders_capture_manifest_configuration_and_runtime_behavior()
    {
        StatesmanDeclaration declaration = BuildRichDeclaration();

        Assert.Equal("app", declaration.Manifest.Id);
        Assert.Equal("2.0", declaration.Manifest.Version);
        Assert.Equal("platform", declaration.Manifest.Metadata["owner"]);

        StateContainerManifest users = declaration.Manifest.Containers.Single(container => container.Path == new StatePath("users"));
        Assert.Equal(StateContainerIsolation.Isolated, users.Isolation);
        Assert.Equal("Users", users.Description);
        Assert.Equal("identity", users.Tags["team"]);

        StateDefinitionManifest account = declaration.Manifest.States.Single(state => state.Path == Account.Path);
        Assert.True(account.IsPartitioned);
        Assert.Equal(2, account.SchemaVersion);
        Assert.Equal("profile", account.Store);
        Assert.Equal("Account state", account.Description);
        Assert.Equal("profile", account.Tags["surface"]);
        Assert.Equal(StateFaultBehavior.KeepLastKnown, account.FaultBehavior);
        Assert.Equal(StateWriteBehavior.RecordAll, account.WriteBehavior);
        Assert.Equal(TimeSpan.MaxValue, account.Freshness.FreshFor);
        Assert.Equal(TimeSpan.Zero, account.Freshness.ServeStaleFor);
        Assert.False(account.Freshness.RefreshStaleInBackground);
        Assert.Equal(9, account.Retention.MaxRevisions);
        Assert.Equal(TimeSpan.FromDays(2), account.Retention.MaxAge);
        Assert.Equal(512, account.Retention.MaxBytes);
        Assert.True(account.Retention.KeepTombstones);
        Assert.True(account.Refresh.OnFirstRead);
        Assert.True(account.Refresh.WhenStale);
        Assert.True(account.Refresh.WarmOnStart);
        Assert.Equal(TimeSpan.FromMinutes(3), account.Refresh.Interval);
        Assert.Equal(3, account.Refresh.Signals.Count);
        Assert.Contains("root.changed", account.Refresh.Signals);
        Assert.Contains("user.changed", account.Refresh.Signals);
        Assert.Contains("user.created", account.Refresh.Signals);
        Assert.Equal(new[] { new StatePartition("east"), new StatePartition("west") }, account.Refresh.WarmPartitions);
        Assert.Equal(StateSourceExecution.Parallel, account.SourceExecution);
        Assert.Equal(StateSourceFailureMode.RequireAll, account.SourceFailureMode);
        Assert.Collection(
            account.Sources,
            source =>
            {
                Assert.Equal("primary \"directory\"", source.Name);
                Assert.Equal(0, source.Order);
                Assert.Equal("primary", source.Metadata["source"]);
            },
            source =>
            {
                Assert.Equal("roles", source.Name);
                Assert.Equal(1, source.Order);
                Assert.Equal("facet", source.Metadata["kind"]);
            });
        Assert.Collection(
            account.Interactions,
            interaction =>
            {
                Assert.Equal("rename", interaction.Name);
                Assert.Equal("Rename account", interaction.Description);
            });

        StateDefinitionManifest theme = declaration.Manifest.States.Single(state => state.Path == Theme.Path);
        Assert.Equal("scoped", theme.Store);

        var directory = new FakeDirectory();
        var time = new ManualTimeProvider();
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(
            declaration,
            services => services.Add(directory),
            stores:
            [
                new InMemoryStateLedgerStore("memory", time),
                new InMemoryStateLedgerStore("profile", time),
                new InMemoryStateLedgerStore("scoped", time),
                new InMemoryStateLedgerStore("shared", time),
            ],
            time: time);
        IState<AccountState> state = harness.Runtime.State(Account, "east");

        IStateSnapshot<AccountState> loaded = await state.RefreshAsync();
        Assert.Equal("User-east", loaded.RequiredValue.DisplayName);
        Assert.Equal(new[] { "reader", "operator" }, loaded.RequiredValue.Roles);

        IStateSnapshot<AccountState> firstSet = await state.SetAsync(loaded.RequiredValue);
        IStateSnapshot<AccountState> secondSet = await state.SetAsync(loaded.RequiredValue);
        Assert.Equal(2, firstSet.Revision);
        Assert.Equal(3, secondSet.Revision);

        StateInteractionRejectedException required = await Assert.ThrowsAsync<StateInteractionRejectedException>(async () =>
            await state.DispatchAsync(new Rename("")));
        StateInteractionRejectedException blocked = await Assert.ThrowsAsync<StateInteractionRejectedException>(async () =>
            await state.DispatchAsync(new Rename("blocked")));
        IStateSnapshot<AccountState> renamed = await state.DispatchAsync(new Rename("renamed"));

        Assert.Contains("required", required.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Blocked", blocked.Message, StringComparison.Ordinal);
        Assert.Equal("renamed!", renamed.RequiredValue.DisplayName);
    }

    [Fact]
    public void Builder_guards_reject_invalid_configuration_and_reuse()
    {
        Assert.Throws<StateDeclarationException>(() =>
            global::Statesman.Statesman.Declare("invalid-container").Container("", _ => { }));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            global::Statesman.Statesman.Declare("invalid-schema")
                .State(Account, state => state.SchemaVersion(0)));

        Assert.Throws<StateDeclarationException>(() =>
            global::Statesman.Statesman.Declare("broken-interaction")
                .State(Account, state => state.Interact<Rename>("rename", _ => { })));

        StateSourceBuilder<AccountState, string>? source = null;
        InvalidOperationException sourceReuse = Assert.Throws<InvalidOperationException>(() =>
            global::Statesman.Statesman.Declare("reuse")
                .State(Account, state => state
                    .Initial(new AccountState("", Array.Empty<string>()))
                    .Load(load =>
                    {
                        source = load.From<FakeDirectory, string>(
                            "name",
                            (service, _, _) => ValueTask.FromResult(service.LoadDisplayName("default")));
                        source.Tag("kind", "primary").Into((current, name, _) => current with { DisplayName = name });
                        source.Tag("kind", "secondary");
                    })));

        Assert.NotNull(source);
        Assert.Contains("already been composed", sourceReuse.Message);

        StatesmanDeclarationBuilder builder = global::Statesman.Statesman.Declare("single-build")
            .State<int>("value", state => state.Initial(0));
        _ = builder.Build();

        InvalidOperationException built = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Contains("only build once", built.Message);
    }

    [Fact]
    public void Manifest_exporter_emits_compact_json_and_nested_mermaid_graphs()
    {
        StatesmanDeclaration declaration = BuildRichDeclaration(id: "graph\"app");

        string json = StatesmanManifestExporter.ToJson(declaration.Manifest, indented: false);
        string mermaid = StatesmanManifestExporter.ToMermaid(declaration.Manifest);

        Assert.Contains("\"id\":\"graph\\u0022app\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.NewLine, json, StringComparison.Ordinal);
        Assert.Contains("flowchart LR", mermaid, StringComparison.Ordinal);
        Assert.Contains("Statesman: graph'app", mermaid, StringComparison.Ordinal);
        Assert.Contains("users/settings", mermaid, StringComparison.Ordinal);
        Assert.Contains("(isolated)", mermaid, StringComparison.Ordinal);
        Assert.Contains("primary 'directory'", mermaid, StringComparison.Ordinal);
        Assert.Contains("AccountState", mermaid, StringComparison.Ordinal);
        Assert.Contains("partitioned", mermaid, StringComparison.Ordinal);
    }

    private static StatesmanDeclaration BuildRichDeclaration(string id = " App ") =>
        global::Statesman.Statesman.Declare(id, " 2.0 ")
            .Defaults(defaults => defaults
                .StoreWith(" shared ")
                .Freshness(freshness => freshness
                    .FreshFor(TimeSpan.FromMinutes(1))
                    .ServeStaleFor(TimeSpan.FromMinutes(5))
                    .StaleWhileRevalidate())
                .Retain(retention => retention
                    .Last(5)
                    .For(TimeSpan.FromDays(1))
                    .UpToBytes(2_048)
                    .KeepTombstones(false))
                .Refresh(refresh => refresh
                    .OnFirstRead()
                    .WhenStale()
                    .Every(TimeSpan.FromMinutes(10))
                    .OnSignal(" root.changed ", "ROOT.CHANGED", " ")
                    .WarmOnStart("alpha", "beta", "alpha"))
                .KeepLastKnownOnFault(false)
                .SuppressEquivalentWrites())
            .Metadata(" owner ", "platform")
            .Container(" users ", users => users
                .Isolated()
                .Describe(" Users ")
                .Tag(" team ", "identity")
                .Defaults(defaults => defaults.StoreWith(" scoped "))
                .State(Account, state => state
                    .Partitioned()
                    .Singleton()
                    .Partitioned()
                    .SchemaVersion(2)
                    .Initial(_ => new AccountState("", Array.Empty<string>()))
                    .StoreWith(" profile ")
                    .Freshness(freshness => freshness.NeverExpires())
                    .Retain(retention => retention
                        .Forever()
                        .Last(9)
                        .For(TimeSpan.FromDays(2))
                        .UpToBytes(512)
                        .KeepTombstones())
                    .Refresh(refresh => refresh
                        .OnFirstRead(false)
                        .OnFirstRead()
                        .WhenStale(false)
                        .WhenStale()
                        .Every(TimeSpan.FromMinutes(3))
                        .OnSignal(" user.changed ", " user.created ")
                        .WarmOnStart("east", "west", "east"))
                    .Load(load =>
                    {
                        load.Sequentially().BestEffort();
                        load.From<FakeDirectory, string>(
                                " primary \"directory\" ",
                                (service, context, _) => ValueTask.FromResult(service.LoadDisplayName(context.Address.Partition.Value)))
                            .Tag(" source ", "primary")
                            .Into((current, name, _) => current with { DisplayName = name });
                        load.From<FakeDirectory, IReadOnlyList<string>>(
                                " roles ",
                                (service, context, _) => ValueTask.FromResult(service.LoadRoles(context.Address.Partition.Value)))
                            .Tag(" kind ", "facet")
                            .IntoAsync((current, roles, _, _) => ValueTask.FromResult(current with { Roles = roles }));
                        load.InParallel().RequireAll();
                    })
                    .Interact<Rename>(" rename ", interaction => interaction
                        .Describe(" Rename account ")
                        .Require((current, command) => !string.IsNullOrWhiteSpace(command.DisplayName), "Display name is required.")
                        .Require((_, command, _, _) => ValueTask.FromResult<string?>(
                            string.Equals(command.DisplayName, "blocked", StringComparison.Ordinal) ? "Blocked." : null))
                        .Reduce((current, command) => current with { DisplayName = command.DisplayName })
                        .Reduce((current, command, context) => current with { DisplayName = $"{command.DisplayName}:{context.Address.Partition.Value}" })
                        .ReduceAsync((current, command, _, _) => ValueTask.FromResult(current with { DisplayName = $"{command.DisplayName}!" })))
                    .Invariant("Display name is required.", value => !string.IsNullOrWhiteSpace(value.DisplayName))
                    .CompareWith(AccountStateComparer.Instance)
                    .SuppressEquivalentWrites(false)
                    .KeepLastKnownOnFault()
                    .MigrateFrom<LegacyAccountState>(1, previous => new AccountState(previous.Name, previous.Roles))
                    .Describe(" Account state ")
                    .Tag(" surface ", "profile"))
                .Container(" settings ", settings => settings
                    .Attached()
                    .Describe(" Settings ")
                    .Tag(" area ", "preferences")
                    .State(Theme, state => state.Initial("light"))))
            .Build();

    [ManagedState]
    private sealed record AccountState(string DisplayName, IReadOnlyList<string> Roles);

    private sealed record LegacyAccountState(string Name, IReadOnlyList<string> Roles);

    private sealed record Rename(string DisplayName);

    private sealed class FakeDirectory
    {
        public string LoadDisplayName(string partition) => $"User-{partition}";

        public IReadOnlyList<string> LoadRoles(string partition) =>
            new[] { "reader", partition == "west" ? "admin" : "operator" };
    }

    private sealed class AccountStateComparer : IEqualityComparer<AccountState>
    {
        public static AccountStateComparer Instance { get; } = new();

        public bool Equals(AccountState? x, AccountState? y) =>
            string.Equals(x?.DisplayName, y?.DisplayName, StringComparison.Ordinal) &&
            (x?.Roles ?? Array.Empty<string>()).SequenceEqual(y?.Roles ?? Array.Empty<string>());

        public int GetHashCode(AccountState obj) => HashCode.Combine(obj.DisplayName, obj.Roles.Count);
    }
}
