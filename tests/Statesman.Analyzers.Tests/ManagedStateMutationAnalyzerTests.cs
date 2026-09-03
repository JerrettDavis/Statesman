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
}
