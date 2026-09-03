using Statesman.Testing;

namespace Statesman.Tests;

public sealed class DeclarationTests
{
    [Fact]
    public void Equivalent_declarations_have_the_same_fingerprint()
    {
        StatesmanDeclaration first = BuildDeclaration();
        StatesmanDeclaration second = BuildDeclaration();

        Assert.Equal(first.Manifest.Fingerprint, second.Manifest.Fingerprint);
        Assert.Equal(2, first.Manifest.States.Count);
        Assert.Contains("users/profile", StatesmanManifestExporter.ToJson(first.Manifest));
        Assert.Contains("flowchart", StatesmanManifestExporter.ToMermaid(first.Manifest));
        StateDefinitionManifest roles = first.Manifest.States.Single(state => state.Path == new StatePath("users/roles"));
        Assert.Equal(
            "System.Collections.Generic.IReadOnlyList<System.String>",
            roles.ValueType);
        Assert.DoesNotContain("Version=", StatesmanManifestExporter.ToJson(first.Manifest));
    }

    [Fact]
    public void Duplicate_state_paths_are_rejected()
    {
        StatesmanDeclarationBuilder builder = global::Statesman.Statesman.Declare("duplicates")
            .State<int>("value", state => state.Initial(0));

        StateDeclarationException exception = Assert.Throws<StateDeclarationException>(() =>
            builder.State<int>("value", state => state.Initial(1)));

        Assert.Contains("more than once", exception.Message);
    }

    private static StatesmanDeclaration BuildDeclaration() =>
        global::Statesman.Statesman.Declare("application", "1.0")
            .Defaults(defaults => defaults
                .StoreWith("memory")
                .Freshness(freshness => freshness.FreshFor(TimeSpan.FromMinutes(5)))
                .Retain(retention => retention.Last(100)))
            .Metadata("owner", "platform")
            .Container("users", users => users
                .Describe("User service state")
                .State<UserProfile>("profile", state => state
                    .Partitioned()
                    .Initial(new UserProfile(string.Empty, string.Empty)))
                .State<IReadOnlyList<string>>("roles", state => state
                    .Partitioned()
                    .Initial(Array.Empty<string>())))
            .Build();

    private sealed record UserProfile(string Id, string DisplayName);
}

public sealed class ContainerViewTests
{
    private static readonly StateKey<int> UserCount = StateKey.Define<int>("users/count");
    private static readonly StateKey<int> OrderCount = StateKey.Define<int>("orders/count");

    [Fact]
    public async Task Container_views_scope_access_and_capture()
    {
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("containers")
            .Container("users", users => users
                .Isolated()
                .State(UserCount, state => state.Initial(0)))
            .Container("orders", orders => orders
                .State(OrderCount, state => state.Initial(0)))
            .Build();
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(declaration);
        IStateContainer users = harness.Runtime.Container("users");

        await users.State(UserCount).SetAsync(3, cancellationToken: CancellationToken.None);
        StateSnapshotSet capture = await users.CaptureAsync(new[] { UserCount.At(StatePartition.Default) }, cancellationToken: CancellationToken.None);

        Assert.Equal(StateContainerIsolation.Isolated, users.Isolation);
        Assert.Single(capture.Snapshots);
        Assert.Throws<StateDeclarationException>(() => users.State(OrderCount));
    }
}
