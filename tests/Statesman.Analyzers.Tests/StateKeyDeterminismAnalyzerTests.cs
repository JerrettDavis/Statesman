using Microsoft.CodeAnalysis;
using Statesman.Analyzers;

namespace Statesman.Analyzers.Tests;

public sealed class StateKeyDeterminismAnalyzerTests
{
    [Fact]
    public async Task Dynamic_key_is_reported_but_constant_key_is_allowed()
    {
        const string source = """
            namespace Statesman
            {
                public readonly struct StateKey<T> { }
                public static class StateKey
                {
                    public static StateKey<T> Define<T>(string value) => default;
                }
            }

            public static class Keys
            {
                public static object Dynamic(string prefix) => Statesman.StateKey.Define<int>(prefix + "/value");
                public static object Constant() => Statesman.StateKey.Define<int>("counter/value");
            }
            """;

        var diagnostics = await AnalyzerTestHost.AnalyzeAsync(source, new StateKeyDeterminismAnalyzer());

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("STM003", diagnostic.Id);
    }
}
