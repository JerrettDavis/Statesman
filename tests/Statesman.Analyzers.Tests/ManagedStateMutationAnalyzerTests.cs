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
    public async Task The_walk_still_honours_every_ignore_and_stops_at_unowned_receivers(string body)
    {
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
