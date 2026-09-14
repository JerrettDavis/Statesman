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
        // A member typed as the interface itself, not as a class implementing it. AllInterfaces does
        // not list a type among its own interfaces, so only the `IsCollectionInterface(type)`
        // operand answers for this one. Finding I5.
        "s.Coll.Add(1);",
        // Parenthesized receivers. The first of these does NOT exercise the walk's
        // ParenthesizedExpressionSyntax step, because GetSymbolInfo on a parenthesized expression
        // answers with the inner expression's symbol, so the walk finds `Items` on Bag without ever
        // stepping. The second does: the parenthesis has to be entered before `s.Plain` can be
        // reached, and with the step removed the mutation is silent. Finding I5.
        "(s.Items).Add(1);",
        "(s.Plain.Items)[0] = 1;",
        // The one shape that makes the walk's ConditionalAccessExpressionSyntax step live: a
        // conditional access is never a receiver on its own, because a null-conditional invocation
        // hands the walk a member BINDING instead, but parenthesizing one and indexing it puts the
        // conditional access itself on the chain. Finding I5.
        "(s.Plain?.Items)[0] = 1;",
        // Null-conditional receivers. The walk's MemberBindingExpressionSyntax arm is what reaches
        // `s.Plain` from the binding `.Items`; without it the second of these was a false negative.
        // Finding I4.
        "s?.Items.Add(1);",
        "s.Plain?.Items.Add(1);",
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
        // A look-alike ICollection<T> in the wrong namespace. Only the namespace half of
        // IsCollectionInterface separates it from the real one. Finding I5.
        "s.Decoyed.Add(1);",
        // A ref-returning indexer: the assignment compiles, the indexer has no set accessor, and
        // the declaring type does implement ICollection<int>, so only the `SetMethod is not null`
        // operand keeps this silent. Finding I5.
        "s.Refs[0] = 1;",
        // An ignored nested [ManagedState] type. The walk stops at the ignored link instead of
        // continuing outward to Bag. Finding I2, option (a).
        "s.Ig.Items.Add(1);",
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

    [Theory]
    [InlineData("s.Map[\"k\"] = 1;")]
    [InlineData("s.Map[\"k\"] += 1;")]
    [InlineData("s.Items[0] = 1;")]
    [InlineData("s.Items[0]++;")]
    public async Task A_collection_indexer_assignment_is_STM004s_alone_and_never_also_STM001(string body)
    {
        // Both rules ran on these spans and both reported: STM001 resolved the Dictionary<,> or
        // List<T> indexer, walked past its unmanaged declaring type, found `Map` on Bag and warned
        // about a member called 'this[]'. That defeats the guide's own stated reason for two ids —
        // a consumer who suppressed STM004 because they accept in-place collection mutation still
        // got a warning on exactly that line, from a rule documented as being about assignment.
        // Finding I1.
        string source = ManagedStateFixture.Consumer(body);
        Assert.Empty(AnalyzerTestHost.CompileErrors(source).Select(diagnostic => diagnostic.ToString()));

        IEnumerable<string> ids = await IdsAsync(body);

        Assert.Contains("STM004", ids);
        Assert.DoesNotContain("STM001", ids);
    }

    [Fact]
    public async Task The_message_keeps_a_null_conditional_receiver_instead_of_starting_with_a_dot()
    {
        // The invocation's own expression in `s?.Items.Add(1)` is the bare binding `.Items.Add`, so
        // building the description from it rendered a user-visible message that began with a dot.
        // The description is now taken from the outermost enclosing conditional access. Finding I4.
        string source = ManagedStateFixture.Consumer("s?.Items.Add(1);");
        Assert.Empty(AnalyzerTestHost.CompileErrors(source).Select(diagnostic => diagnostic.ToString()));

        ImmutableArray<Diagnostic> diagnostics = await AnalyzerTestHost.AnalyzeAsync(
            source,
            new ManagedStateCollectionMutationAnalyzer());

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("STM004", diagnostic.Id);
        Assert.Equal(
            "'s?.Items.Add' mutates the collection held by managed state 'Bag' and should be "
                + "changed through IState<T>, an interaction reducer, or an explicit StateMutationBoundary",
            diagnostic.GetMessage());
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
