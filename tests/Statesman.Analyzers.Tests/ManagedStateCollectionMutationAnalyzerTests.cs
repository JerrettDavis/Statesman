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
        // Types that implement only the NON-GENERIC System.Collections.ICollection. Every row here
        // is silent without that operand of IsCollectionInterface. ROADMAP 0.3 Phase 21.
        "s.Fifo.Enqueue(1);",
        "_ = s.Fifo.Dequeue();",
        "s.Lifo.Push(1);",
        "_ = s.Linked.AddLast(1);",
        "s.ConcurrentFifo.Enqueue(1);",
        "s.ConcurrentUnordered.Add(1);",
        "s.Blocking.Add(1);",
        "s.Blocking.CompleteAdding();",
        "s.Legacy.Add(1);",
        "s.LegacyMap[\"k\"] = 1;",
        "s.LegacyList.Add(1);",
        // Types that already passed the type test and whose mutating member was missing from the
        // name set. ObservableCollection<T>.Add fired while .Move on the same member did not.
        "s.Observable.Move(0, 1);",
        "s.Sorted.RemoveWhere(x => x > 0);",
        "_ = s.ConcurrentMap.AddOrUpdate(\"k\", 1, (a, b) => b);",
        "_ = s.ConcurrentMap.TryRemove(\"k\", out _);",
        // Conversions on the receiver chain. One row per arm the walk gained: an explicit cast,
        // an `as` expression, and the null-forgiving operator. Research finding D2.
        "((List<int>)s.Coll).Add(1);",
        "((ICollection<int>)s.Readonlies).Add(1);",
        "(s.Coll as List<int>).Add(1);",
        "(s.Coll as List<int>)!.Add(1);",
        // One hop of alias tracking. Each of these resolves the local to its sole initializer and
        // re-walks it. Design option (ii); the corresponding bail-outs are in Silent below.
        "var a = s.Items; a.Add(1);",
        "List<int> b = s.Items; b.Add(1);",
        "var c = s.Map; c[\"k\"] = 1;",
        "var d = s.Slots; d[0] = 1;",
        // A lambda that only READS the alias is NOT a bail-out: the capture defers the mutation, it
        // does not change which object is mutated. Suppressing this would lose a true positive.
        "var e = s.Items; Capture(() => e.Add(1));",
        // Two hops, not one: the walk re-dispatches on whatever SingleInitializerOf returns, so a
        // chain of aliases resolves fully rather than stopping after the first hop. Review finding,
        // fix round 1.
        "var r = s.Items; var t = r; t.Add(1);",
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
        // A name in the mutator set on a type that is no collection at all. Only the type test
        // keeps these silent, and Take is the name that makes the point: it is also
        // Enumerable.Take, whose declaring type is System.Linq.Enumerable.
        "s.Builder.Clear();",
        "_ = s.Items.Take(2);",
        // A name that is NOT in the mutator set, on a type that passes the type test. CopyTo
        // mutates its argument rather than the receiver, so the name set is what holds this one.
        "s.LegacyCollection.CopyTo(new int[1], 0);",
        // A user-defined explicit conversion on the receiver chain. The conversion invokes a static
        // method that returns an unrelated Exported instance, not a view onto Plain, so the write
        // lands on that object rather than on anything owned by Bag. Review finding, fix round 1.
        "((Exported)s.Plain).Items.Add(1);",
        // The alias bail-outs, one row per rule in SingleInitializerOf. Without each rule the row
        // beside it starts reporting, which is what its lever proves.
        "var f = s.Items; f = new List<int>(); f.Add(1);",
        "var g = s.Items; Rebind(out g); g.Add(1);",
        "foreach (var h in s.Plains) { h.Items.Add(1); }",
        "var (i, _) = (s.Items, 0); i.Add(1);",
        "if (s.Coll is List<int> j) { j.Add(1); }",
        "var k = s.Counted; k++; k.Add(1);",
        // A rebind performed INSIDE a lambda is still an assignment, so the reassignment check
        // catches it and no separate lambda bail-out is needed.
        "var m = s.Items; Capture(() => m = new List<int>()); m.Add(1);",
        // A deconstructing assignment into an EXISTING local rebinds it too, not only a declaration:
        // `n` is not itself the assignment's Left, only a tuple element of it, so the rebind check
        // must climb through the tuple and argument wrappers to see that. Review finding, fix round 1.
        "var n = s.Items; (n, _) = (new List<int>(), 0); n.Add(1);",
        // A `ref` local aliasing an existing local IS a rebind, not a mutation of what the local
        // referred to: `ref var r = ref a; r = new List<int>();` replaces the object `a` refers to,
        // so the walk must bail on this shape the same way it bails on a direct reassignment. Before
        // the fix, `ref a` is a RefExpressionSyntax, which neither the reassignment check nor the
        // out/ref ARGUMENT check sees, so the walk resolved `a` back to Items and reported on a
        // fresh, unmanaged object. Final review, fix wave, finding I1.
        "var a = s.Items; ref var r = ref a; r = new List<int>(); a.Add(1);",
        // A lambda that rebinds through a block body rather than an expression body. The existing
        // row above (`Capture(() => m = new List<int>())`) already covers the expression-bodied
        // shape; this pins the block-bodied one too, since both are still assignments the
        // reassignment check catches.
        "var o = s.Items; Capture(() => { o = new List<int>(); }); o.Add(1);",
    ];

    /// <summary>
    /// Alias chains named by the review as candidates for looping the ownership walk. Neither
    /// compiles: a local used in its own initializer is CS0165 (use of unassigned local variable),
    /// and referencing a local before its own declaration point is CS0841. An analyzer runs
    /// continuously against half-typed IDE code though, so an uncompilable cycle can still reach the
    /// walk. Only the first row actually loops without the walk's visited-node guard: removing the
    /// guard times this row out. The second row does NOT loop even without the guard, which differs
    /// from what the review predicted — the pre-declaration read of <c>b</c> does not bind to a local
    /// symbol under CS0841 error recovery, so <c>SingleInitializerOf</c>'s own <c>ILocalSymbol</c>
    /// check already returns null before any cycle can form. Both rows stay here as a regression
    /// pin, and the guard remains in place regardless, since it is a correct defence for the shape
    /// the first row exercises and costs nothing for the shape the second row exercises. Review
    /// finding, fix round 1.
    /// </summary>
    public static TheoryData<string> CyclicAliasChains =>
    [
        "List<int> x = x; x.Add(1);",
        "var a = b; var b = a; a.Add(1);",
    ];

    [Theory]
    [MemberData(nameof(CyclicAliasChains))]
    public async Task A_cyclic_alias_chain_does_not_hang_the_ownership_walk(string body)
    {
        string source = ManagedStateFixture.Consumer(body);
        Task<ImmutableArray<Diagnostic>> analysis = AnalyzerTestHost.AnalyzeAsync(
            source,
            new ManagedStateMutationAnalyzer(),
            new ManagedStateCollectionMutationAnalyzer());

        Task winner = await Task.WhenAny(
            analysis,
            Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.Same(analysis, winner);
        ImmutableArray<Diagnostic> diagnostics = await analysis;
        Assert.DoesNotContain("STM004", diagnostics.Select(diagnostic => diagnostic.Id));
    }

    private static async Task<IEnumerable<string>> IdsAsync(string consumerBody)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerTestHost.AnalyzeAsync(
            ManagedStateFixture.Consumer(consumerBody),
            new ManagedStateMutationAnalyzer(),
            new ManagedStateCollectionMutationAnalyzer());
        return diagnostics.Select(diagnostic => diagnostic.Id).ToArray();
    }

    private static async Task<IEnumerable<string>> TopLevelIdsAsync(string consumerBody)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerTestHost.AnalyzeTopLevelAsync(
            ManagedStateFixture.TopLevelConsumer(consumerBody),
            new ManagedStateMutationAnalyzer(),
            new ManagedStateCollectionMutationAnalyzer());
        return diagnostics.Select(diagnostic => diagnostic.Id).ToArray();
    }

    [Fact]
    public async Task An_alias_hop_in_a_top_level_statements_file_reports_STM004()
    {
        // A local declared at the top level of a file has no enclosing BlockSyntax, so before the
        // compilation-unit scope fallback the alias walk answered null here and the hop was silent
        // while the direct shape on the very next line still reported. That asymmetry matters more
        // than a corner case: samples/Statesman.Sample.Migration, the repository's only analyzer
        // consumer, is itself a top-level-statements file.
        string body = "var alias = s.Items; alias.Add(1);";
        Assert.Empty(
            AnalyzerTestHost.CompileErrorsTopLevel(ManagedStateFixture.TopLevelConsumer(body))
                .Select(diagnostic => diagnostic.ToString()));

        Assert.Contains("STM004", await TopLevelIdsAsync(body));
    }

    [Fact]
    public async Task An_alias_hop_in_a_top_level_statements_file_reports_STM001()
    {
        // The STM001 half of the same gap, on the same scope fallback: both rules resolve ownership
        // through ManagedStateOwnership, so a scope the walk cannot see silences both.
        string body = "var plain = s.Plain; plain.Value = 1;";
        Assert.Empty(
            AnalyzerTestHost.CompileErrorsTopLevel(ManagedStateFixture.TopLevelConsumer(body))
                .Select(diagnostic => diagnostic.ToString()));

        Assert.Contains("STM001", await TopLevelIdsAsync(body));
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

    [Fact]
    public async Task A_deconstructing_assignment_into_a_managed_member_reports_STM004_alone_and_never_STM001()
    {
        // `(q.Value, _) = (1, 0)` deconstructs into a member access, not a local: STM001's
        // AnalyzeMutation reads assignment.Left and finds a TupleExpressionSyntax rather than the
        // MemberAccessExpressionSyntax it expects, so it never fires here, while `q.Items.Add(1)` on
        // the next line reaches STM004 normally through the alias walk. Pre-existing gap, not
        // introduced by this phase; final review finding M5, explicitly parked. This pins the
        // current, correct-for-STM004 truth: exactly one diagnostic, STM004, nothing on the
        // deconstructing statement.
        string body = "var q = s.Plain; (q.Value, _) = (1, 0); q.Items.Add(1);";
        Assert.Empty(
            AnalyzerTestHost.CompileErrors(ManagedStateFixture.Consumer(body)).Select(d => d.ToString()));

        IEnumerable<string> ids = await IdsAsync(body);

        Assert.Single(ids, id => id == "STM004");
        Assert.DoesNotContain("STM001", ids);
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

    [Theory]
    [InlineData("s.Changed += (a, b) => { };")]
    [InlineData("s.Changed -= (a, b) => { };")]
    [InlineData("s.Plain.Changed += (a, b) => { };")]
    public async Task Subscribing_to_an_event_on_managed_state_is_silent(string body)
    {
        // STM004 is silent here for a different reason from STM001's. `+=` IS one of the assignment
        // kinds this analyzer registers for, so AnalyzeElementTarget does run on `s.Changed` -- and
        // returns immediately, because the target is a member access rather than an
        // ElementAccessExpressionSyntax. The symbol test behind it would refuse an IEventSymbol too,
        // so the silence rests on two independent guards. ROADMAP 0.3 Phase 21.
        string source = ManagedStateFixture.Consumer(body);
        Assert.Empty(AnalyzerTestHost.CompileErrors(source).Select(diagnostic => diagnostic.ToString()));

        ImmutableArray<Diagnostic> diagnostics = await AnalyzerTestHost.AnalyzeAsync(
            source,
            new ManagedStateCollectionMutationAnalyzer());

        Assert.DoesNotContain("STM004", diagnostics.Select(diagnostic => diagnostic.Id));
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
