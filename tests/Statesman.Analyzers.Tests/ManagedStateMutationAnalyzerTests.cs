using Microsoft.CodeAnalysis;
using Statesman.Analyzers;

namespace Statesman.Analyzers.Tests;

public sealed class ManagedStateMutationAnalyzerTests
{
    [Fact]
    public async Task Direct_mutation_and_mutable_shape_are_reported()
    {
        const string source = """
            using System;
            namespace Statesman
            {
                [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
                public sealed class ManagedStateAttribute : Attribute { }
                [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Constructor)]
                public sealed class StateMutationBoundaryAttribute : Attribute { }
                [AttributeUsage(AttributeTargets.All)]
                public sealed class StateMutationAnalysisIgnoreAttribute : Attribute { }
            }

            [Statesman.ManagedState]
            public sealed class Counter
            {
                public int Value { get; set; }
            }

            public static class Consumer
            {
                public static void Mutate(Counter counter) => counter.Value = 2;
            }
            """;

        var diagnostics = await AnalyzerTestHost.AnalyzeAsync(source, new ManagedStateMutationAnalyzer());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "STM001");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "STM002");
    }

    [Fact]
    public async Task Explicit_boundary_allows_legacy_adapter_mutation()
    {
        const string source = """
            using System;
            namespace Statesman
            {
                [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
                public sealed class ManagedStateAttribute : Attribute { }
                [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Constructor)]
                public sealed class StateMutationBoundaryAttribute : Attribute { }
                [AttributeUsage(AttributeTargets.All)]
                public sealed class StateMutationAnalysisIgnoreAttribute : Attribute { }
            }

            [Statesman.ManagedState]
            public sealed class Counter
            {
                public int Value { get; set; }
            }

            public static class Adapter
            {
                [Statesman.StateMutationBoundary]
                public static void Import(Counter counter) => counter.Value = 2;
            }
            """;

        var diagnostics = await AnalyzerTestHost.AnalyzeAsync(source, new ManagedStateMutationAnalyzer());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "STM001");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "STM002");
    }

    [Theory]
    [InlineData("s.Scalar = 1;", "Scalar", "Bag")]
    [InlineData("s.Child.Value = 1;", "Value", "ChildBag")]
    [InlineData("s.Plain.Value = 1;", "Value", "Bag")]
    // The managed type's OWN indexer setter. The indexer guard finding I1 added skips an indexer
    // whose declaring type is not itself managed state, because STM004 owns that shape; this one is
    // declared ON Bag, so it stays STM001's and no other rule covers it.
    [InlineData("s[0] = 1;", "this[]", "Bag")]
    // An indexer link mid-chain: the walk's ElementAccessExpressionSyntax step is what carries it
    // from `s.Plains[0]` to `s.Plains`, where `Plains` is declared on the managed Bag.
    [InlineData("s.Plains[0].Value = 1;", "Value", "Bag")]
    // A null-conditional assignment, which C# 14 makes legal — measured, not assumed: the review's
    // prediction that this shape is still CS0131 does not hold on this language version, so the
    // rule does have to answer for it. The receiver `.Plain` arrives as a member BINDING and is
    // itself declared on the managed Bag, so the walk answers at that link without stepping; the
    // arm finding I4 added is what carries the harder shape, `s.Plain?.Items.Add(1)`.
    [InlineData("s?.Plain.Value = 1;", "Value", "Bag")]
    // A conversion on the receiver chain, which broke the walk for BOTH rules until Phase 21 added
    // the cast, `as` and null-forgiving arms. Research finding D2.
    [InlineData("((PlainBag)s.Plain).Value = 1;", "Value", "Bag")]
    [InlineData("(s.Plain as PlainBag).Value = 1;", "Value", "Bag")]
    [InlineData("((PlainBag)s.Plain)!.Value = 1;", "Value", "Bag")]
    public async Task A_write_reached_through_managed_state_reports_STM001_and_names_its_owner(
        string body,
        string member,
        string owner)
    {
        // `s.Plain.Value = 1` is the shape the receiver-chain walk added: PlainBag carries no
        // [ManagedState], so before Phase 20 the immediately-containing-type test found nothing and
        // the write was silent while `s.Plain.Items.Add(1)` reported STM004. The two rules now agree.
        string source = ManagedStateFixture.Consumer(body);
        Assert.Empty(AnalyzerTestHost.CompileErrors(source).Select(diagnostic => diagnostic.ToString()));

        var diagnostics = await AnalyzerTestHost.AnalyzeAsync(source, new ManagedStateMutationAnalyzer());
        Diagnostic reported = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "STM001");
        Assert.Equal(
            $"'{member}' belongs to managed state '{owner}' and should be changed through IState<T>, "
                + "an interaction reducer, or an explicit StateMutationBoundary",
            reported.GetMessage());
    }

    [Theory]
    [InlineData("s.Hidden.Value = 1;")]
    [InlineData("s.Plain.Exempt = 1;")]
    [InlineData("p.Items.Clear();")]
    // An ignored nested [ManagedState] type. The walk STOPS at an ignored link rather than
    // continuing outward and re-attributing the write to Bag, so an escape hatch silences what it
    // marks and everything reached through it. Finding I2, option (a).
    [InlineData("s.Ig.Value = 1;")]
    // The same rule for an ignored MEMBER of a managed owner, with a further managed link outside
    // it: `Hidden` is ignored on ChildBag, and `Child` on Bag is managed, so only stopping the walk
    // keeps this silent.
    [InlineData("s.Child.Hidden.Value = 1;")]
    // A user-defined explicit conversion on the receiver chain. The conversion invokes a static
    // method that returns an unrelated Exported instance, not a view onto Plain, so the write lands
    // on that object rather than on anything owned by Bag. Review finding, fix round 1.
    [InlineData("((Exported)s.Plain).Value = 1;")]
    // A `ref` local aliasing an existing local IS a rebind, not a mutation of what the local
    // referred to: `ref var r2 = ref q; r2 = new PlainBag();` replaces the object `q` refers to, so
    // `q.Value = 1` afterward lands on a fresh, unmanaged PlainBag. Before the fix, `ref q` is a
    // RefExpressionSyntax, which neither the reassignment check nor the out/ref ARGUMENT check sees.
    // Final review, fix wave, finding I1 (STM004's twin).
    [InlineData("var q = s.Plain; ref var r2 = ref q; r2 = new PlainBag(); q.Value = 1;")]
    public async Task The_walk_still_honours_every_ignore_and_stops_at_unowned_receivers(string body)
    {
        string source = ManagedStateFixture.Consumer(body);
        Assert.Empty(AnalyzerTestHost.CompileErrors(source).Select(diagnostic => diagnostic.ToString()));

        var diagnostics = await AnalyzerTestHost.AnalyzeAsync(source, new ManagedStateMutationAnalyzer());

        Assert.DoesNotContain("STM001", diagnostics.Select(diagnostic => diagnostic.Id));
    }

    [Theory]
    [InlineData("var b = new Bag { Scalar = 1 };")]
    [InlineData("Bag b = new() { Scalar = 1 };")]
    [InlineData("var r = new Rec(0); r = r with { Value = 1 };")]
    public async Task An_initializer_constructs_a_value_rather_than_mutating_a_stored_one(string body)
    {
        // One row per arm of ManagedStateOwnership.IsInitialization: an explicitly typed object
        // creation, a target-typed `new()`, and a record `with`. Each write here IS reached through
        // managed state and would report without the guard, which is why the guard is load-bearing
        // rather than defensive. Finding I5.
        string source = ManagedStateFixture.Consumer(body);
        Assert.Empty(AnalyzerTestHost.CompileErrors(source).Select(diagnostic => diagnostic.ToString()));

        var diagnostics = await AnalyzerTestHost.AnalyzeAsync(source, new ManagedStateMutationAnalyzer());

        Assert.DoesNotContain("STM001", diagnostics.Select(diagnostic => diagnostic.Id));
    }

    [Fact]
    public async Task An_ignored_enclosing_method_is_silent_for_both_rules()
    {
        // IsAllowedBoundary treats [StateMutationAnalysisIgnore] on an enclosing symbol exactly as
        // it treats [StateMutationBoundary]. Both rules call the same helper, so one declaration
        // pins the operand for both. Finding I5.
        const string declaration = """
            public static class Exempted
            {
                [Statesman.StateMutationAnalysisIgnore]
                public static void Import(Bag s)
                {
                    s.Scalar = 1;
                    s.Items.Add(1);
                }
            }
            """;
        string source = ManagedStateFixture.Source + declaration;
        Assert.Empty(AnalyzerTestHost.CompileErrors(source).Select(diagnostic => diagnostic.ToString()));

        var diagnostics = await AnalyzerTestHost.AnalyzeAsync(
            source,
            new ManagedStateMutationAnalyzer(),
            new ManagedStateCollectionMutationAnalyzer());

        Assert.DoesNotContain("STM001", diagnostics.Select(diagnostic => diagnostic.Id));
        Assert.DoesNotContain("STM004", diagnostics.Select(diagnostic => diagnostic.Id));
    }

    [Theory]
    [InlineData("s.Changed += (a, b) => { };")]
    [InlineData("s.Changed -= (a, b) => { };")]
    [InlineData("s.Plain.Changed += (a, b) => { };")]
    public async Task Subscribing_to_an_event_on_managed_state_is_silent(string body)
    {
        // An event add or remove is not a state write: the value the ledger stores does not change,
        // and reporting here would fire on every idiomatic observer wiring. STM001 is silent because
        // AnalyzeMutation's symbol test admits only IPropertySymbol and IFieldSymbol, and an
        // IEventSymbol is neither. That asymmetry is worth pinning rather than leaving implicit,
        // because `s.Plain.Value = 1` on the same plain holder DOES report. ROADMAP 0.3 Phase 21.
        string source = ManagedStateFixture.Consumer(body);
        Assert.Empty(AnalyzerTestHost.CompileErrors(source).Select(diagnostic => diagnostic.ToString()));

        var diagnostics = await AnalyzerTestHost.AnalyzeAsync(source, new ManagedStateMutationAnalyzer());

        Assert.DoesNotContain("STM001", diagnostics.Select(diagnostic => diagnostic.Id));
    }

    [Theory]
    // One element, written directly on the managed type.
    [InlineData("(s.Scalar, _) = (1, 0);", 1)]
    // One element, reached through a local alias, which is the shape Phase 21's parked fact named.
    [InlineData("var q = s.Plain; (q.Value, _) = (1, 0);", 1)]
    // A parenthesized element. It reports either way, because Roslyn resolves a parenthesized
    // expression to its operand's symbol; what the parenthesis arm changes is the reported span,
    // which the next fact pins.
    [InlineData("((s.Child.Value), _) = (1, 0);", 1)]
    // Two managed elements in one deconstruction: each is its own write and each reports.
    [InlineData("(s.Plain.Value, s.Scalar) = (1, 2);", 2)]
    // A nested tuple, so the tuple arm has to recurse rather than decompose one level.
    [InlineData("((s.Plain.Value, s.Scalar), s.Child.Value) = ((1, 2), 3);", 3)]
    public async Task A_deconstructing_assignment_reports_STM001_once_per_managed_element(
        string body,
        int expected)
    {
        // A deconstruction's left-hand side is a TupleExpressionSyntax, whose GetSymbolInfo is
        // neither a property nor a field, so before AnalyzeTarget every write it performed was
        // silent while the same write spelled as a plain assignment reported. ROADMAP 0.3 Phase 21
        // final-review finding M5, parked there and overturned here.
        string source = ManagedStateFixture.Consumer(body);
        Assert.Empty(AnalyzerTestHost.CompileErrors(source).Select(diagnostic => diagnostic.ToString()));

        var diagnostics = await AnalyzerTestHost.AnalyzeAsync(source, new ManagedStateMutationAnalyzer());

        Assert.Equal(expected, diagnostics.Count(diagnostic => diagnostic.Id == "STM001"));
    }

    [Fact]
    public async Task A_parenthesized_deconstruction_element_reports_on_the_write_not_the_parentheses()
    {
        // The one thing AnalyzeTarget's parenthesis arm changes, and therefore the only fact that
        // can discriminate it. Roslyn answers GetSymbolInfo on a ParenthesizedExpressionSyntax with
        // the operand's symbol, so the diagnostic fires with or without the arm; without it the
        // reported span is "(s.Child.Value)", parentheses included, and the squiggle a consumer sees
        // covers punctuation the write does not happen at. Measured both ways.
        const string body = "((s.Child.Value), _) = (1, 0);";
        string source = ManagedStateFixture.Consumer(body);
        Assert.Empty(AnalyzerTestHost.CompileErrors(source).Select(diagnostic => diagnostic.ToString()));

        var diagnostics = await AnalyzerTestHost.AnalyzeAsync(source, new ManagedStateMutationAnalyzer());

        Diagnostic reported = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "STM001");
        Assert.Equal(
            "s.Child.Value",
            source.Substring(reported.Location.SourceSpan.Start, reported.Location.SourceSpan.Length));
    }

    [Theory]
    // A declaration form: `var (a, b)` is a DeclarationExpressionSyntax, not a tuple of targets.
    [InlineData("var (a, b) = (1, 2); System.GC.KeepAlive(a); System.GC.KeepAlive(b);")]
    // The typed declaration form, which IS a tuple, of declaration expressions writing new locals.
    [InlineData("(int c, int d) = (3, 4); System.GC.KeepAlive(c); System.GC.KeepAlive(d);")]
    // Plain locals as targets. The property-or-field test rejects an ILocalSymbol.
    [InlineData("int e = 0; int f = 0; (e, f) = (5, 6); System.GC.KeepAlive(e + f);")]
    // A holder that is not managed state at all.
    [InlineData("var local = new PlainBag(); (local.Value, _) = (1, 0);")]
    // An array element target, which has no symbol of its own.
    [InlineData("var arr = new int[2]; (arr[0], _) = (1, 0);")]
    // Two discards, so no element has a writable symbol.
    [InlineData("(_, _) = (1, 0);")]
    // Behind the two escape hatches: an ignored member and an ignored [ManagedState] type.
    [InlineData("(s.Hidden.Value, _) = (1, 0);")]
    [InlineData("(s.Ig.Value, _) = (1, 0);")]
    // An ignored member of a plain holder owned by managed state.
    [InlineData("(s.Plain.Exempt, _) = (1, 0);")]
    // The unmanaged look-alike parameter.
    [InlineData("(p.Items[0], _) = (1, 0);")]
    public async Task A_deconstructing_assignment_into_anything_unowned_stays_silent(string body)
    {
        // The false-positive sweep for AnalyzeTarget. Every shape here is a deconstruction that the
        // new decomposition reaches and that must still produce nothing, so the widening is proved
        // to be a widening of the reporting rule rather than of its ownership test: the guards that
        // held before AnalyzeTarget existed are the ones that hold each of these rows.
        string source = ManagedStateFixture.Consumer(body);
        Assert.Empty(AnalyzerTestHost.CompileErrors(source).Select(diagnostic => diagnostic.ToString()));

        var diagnostics = await AnalyzerTestHost.AnalyzeAsync(source, new ManagedStateMutationAnalyzer());

        Assert.DoesNotContain("STM001", diagnostics.Select(diagnostic => diagnostic.Id));
    }

    [Fact]
    public async Task A_plain_types_own_constructor_and_own_methods_writing_its_own_members_stay_silent()
    {
        // PlainBag's own ctor writes Value, and Reset() writes Value and clears Items. The receiver is
        // an implicit `this` on a type that is not managed state, so the walk finds no owner and stops.
        // Nothing in the fixture reaches those writes through a [ManagedState] chain.
        var diagnostics = await AnalyzerTestHost.AnalyzeAsync(
            ManagedStateFixture.Source,
            new ManagedStateMutationAnalyzer());

        Assert.Empty(
            AnalyzerTestHost.CompileErrors(ManagedStateFixture.Source).Select(diagnostic => diagnostic.ToString()));
        Assert.DoesNotContain("STM001", diagnostics.Select(diagnostic => diagnostic.Id));
    }
}
