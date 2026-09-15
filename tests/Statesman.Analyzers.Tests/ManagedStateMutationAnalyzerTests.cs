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
