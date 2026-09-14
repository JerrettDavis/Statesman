using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Statesman.Analyzers;

namespace Statesman.Analyzers.Tests;

public sealed class ManagedStateCollectionMutationAnalyzerTests
{
    /// <summary>Consumer bodies that must report STM004.</summary>
    public static TheoryData<string> Mutating =>
    [
        "s.Items.Add(1);",
        "s.Items[0] = 1;",
        "s.Items.Clear();",
        "s.Items.Remove(1);",
        "s.Items.AddRange(new[] { 1 });",
        "s.Items.Insert(0, 1);",
        "s.Items.RemoveAt(0);",
        "s.Items.RemoveAll(x => x > 0);",
        "s.Items.Reverse();",
        "s.Items.Sort();",
        "s.Map[\"k\"] = 1;",
        "s.Map.Add(\"k\", 1);",
        "s.Map.Remove(\"k\");",
        "s.Set.Add(1);",
        "s.Slots[0] = 1;",
        "s.Child.Items.Add(1);",
        "s.Plain.Items.Add(1);",
        "s.FrozenBuilder.Add(1);",
    ];

    /// <summary>Consumer bodies that must stay silent.</summary>
    public static TheoryData<string> Silent =>
    [
        "_ = s.Items.Count;",
        "_ = s.Items.Contains(1);",
        "_ = s.Readonlies.Count;",
        "_ = s.Plain.Items.Count;",
        "s.Frozen.Add(1);",
        "s.Ignored.Add(1);",
        "p.Items.Add(1);",
        "new List<int>().Add(1);",
        "s.Items.ToList().Add(1);",
        "var l = s.Items; l.Add(1);",
    ];

    private static async Task<IEnumerable<string>> IdsAsync(string consumerBody)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerTestHost.AnalyzeAsync(
            ManagedStateFixture.Consumer(consumerBody),
            new ManagedStateMutationAnalyzer(),
            new ManagedStateCollectionMutationAnalyzer());
        return diagnostics.Select(diagnostic => diagnostic.Id).ToArray();
    }

    [Theory]
    [MemberData(nameof(Mutating))]
    public async Task Mutating_a_collection_reached_through_managed_state_reports_STM004(string body)
    {
        Assert.Contains("STM004", await IdsAsync(body));
    }

    [Theory]
    [MemberData(nameof(Silent))]
    public async Task Reading_copying_or_aliasing_a_collection_is_silent(string body)
    {
        Assert.DoesNotContain("STM004", await IdsAsync(body));
    }

    [Fact]
    public void Every_consumer_body_in_this_class_compiles_with_no_compiler_error()
    {
        // The research probe's first fixture named a property `Array`, which shadowed System.Array,
        // produced CS0236 in every case and silenced every diagnostic — an empty diagnostic list that
        // read as "the rule misses everything". This fact is why that cannot happen again.
        foreach (TheoryDataRow<string> row in Mutating.Concat(Silent))
        {
            ImmutableArray<Diagnostic> errors = AnalyzerTestHost.CompileErrors(ManagedStateFixture.Consumer(row.Data));
            Assert.Empty(errors.Select(diagnostic => diagnostic.ToString()));
        }
    }

    [Fact]
    public async Task An_immutable_collections_nested_builder_fires_while_the_collection_itself_does_not()
    {
        // ImmutableList<T>.Builder.Add mutates in place; ImmutableList<T>.Add returns a new list. Only
        // the ContainingType test separates them, because ContainingNamespace for a nested type is the
        // namespace of its containing type — System.Collections.Immutable for both.
        Assert.Contains("STM004", await IdsAsync("s.FrozenBuilder.Add(1);"));
        Assert.DoesNotContain("STM004", await IdsAsync("s.Frozen.Add(1);"));
    }

    [Theory]
    [InlineData("""
        public static class Adapter
        {
            [Statesman.StateMutationBoundary]
            public static void Import(Bag s) => s.Items.Add(1);
        }
        """)]
    [InlineData("""
        public static class Builderish
        {
            public static Bag Make() => new Bag { Items = { 1, 2 } };
        }
        """)]
    [InlineData("""
        [Statesman.ManagedState]
        public sealed class Seeded
        {
            public List<int> Items { get; init; } = new();

            public Seeded() => Items.Add(1);
        }
        """)]
    public async Task A_boundary_an_object_initializer_and_the_managed_constructor_are_all_silent(string declaration)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerTestHost.AnalyzeAsync(
            ManagedStateFixture.Source + declaration,
            new ManagedStateCollectionMutationAnalyzer());

        Assert.Empty(AnalyzerTestHost.CompileErrors(ManagedStateFixture.Source + declaration).Select(d => d.ToString()));
        Assert.DoesNotContain("STM004", diagnostics.Select(diagnostic => diagnostic.Id));
    }

    [Fact]
    public async Task The_managed_types_own_non_constructor_member_is_not_a_boundary()
    {
        const string declaration = """
            [Statesman.ManagedState]
            public sealed class Seeded
            {
                public List<int> Items { get; init; } = new();

                public void Seed() => Items.Add(1);
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzerTestHost.AnalyzeAsync(
            ManagedStateFixture.Source + declaration,
            new ManagedStateCollectionMutationAnalyzer());

        Assert.Contains("STM004", diagnostics.Select(diagnostic => diagnostic.Id));
    }

    [Fact]
    public async Task The_message_names_the_operation_and_the_owning_managed_type()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerTestHost.AnalyzeAsync(
            ManagedStateFixture.Source + """
                public static class Consumer
                {
                    public static void Run(Bag s) => s.Child.Items.Add(1);
                }
                """,
            new ManagedStateCollectionMutationAnalyzer());

        // The owner is the NEAREST managed declarer on the chain, not the outermost one: `Items` is
        // declared on ChildBag, which carries [ManagedState] itself. The walk stops at the first
        // managed link, which is what makes `s.Plain.Items.Add(1)` answer with Bag — `Items` there is
        // declared on the plain PlainBag, so the walk keeps going until it reaches `Plain` on Bag.
        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("STM004", diagnostic.Id);
        Assert.Equal(
            "'s.Child.Items.Add' mutates the collection held by managed state 'ChildBag' and should be "
                + "changed through IState<T>, an interaction reducer, or an explicit StateMutationBoundary",
            diagnostic.GetMessage());
    }
}
