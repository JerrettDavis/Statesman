using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Statesman.Analyzers;
using Statesman.CodeFixes;

namespace Statesman.Analyzers.Tests;

public sealed class MutableShapeCodeFixProviderTests
{
    private static DiagnosticResult MutableShape(int marker, string member) =>
        new DiagnosticResult("STM002", DiagnosticSeverity.Warning)
            .WithLocation(marker)
            .WithArguments(member);

    private static DiagnosticResult DirectMutation(int marker, string member, string stateType) =>
        new DiagnosticResult("STM001", DiagnosticSeverity.Warning)
            .WithLocation(marker)
            .WithArguments(member, stateType);

    [Fact]
    public async Task A_publicly_settable_auto_property_becomes_init_only()
    {
        var test = new CodeFixFixture<ManagedStateMutationAnalyzer, MutableShapeCodeFixProvider>
        {
            TestCode = CodeFixTestHost.Attributes + """

                [Statesman.ManagedState]
                public sealed class Counter
                {
                    public int {|#0:Value|} { get; set; }
                }
                """,
            FixedCode = CodeFixTestHost.Attributes + """

                [Statesman.ManagedState]
                public sealed class Counter
                {
                    public int Value { get; init; }
                }
                """,
            ReferenceAssemblies = CodeFixTestHost.Modern,
        };
        test.ExpectedDiagnostics.Add(MutableShape(0, "Value"));

        await test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_init_fix_compiles_on_netstandard2_0_once_the_IsExternalInit_shim_is_declared()
    {
        var test = new CodeFixFixture<ManagedStateMutationAnalyzer, MutableShapeCodeFixProvider>
        {
            TestCode = CodeFixTestHost.Attributes + """

                namespace System.Runtime.CompilerServices
                {
                    internal static class IsExternalInit { }
                }

                [Statesman.ManagedState]
                public sealed class Counter
                {
                    public int {|#0:Value|} { get; set; }
                }
                """,
            FixedCode = CodeFixTestHost.Attributes + """

                namespace System.Runtime.CompilerServices
                {
                    internal static class IsExternalInit { }
                }

                [Statesman.ManagedState]
                public sealed class Counter
                {
                    public int Value { get; init; }
                }
                """,
            ReferenceAssemblies = ReferenceAssemblies.NetStandard.NetStandard20,
        };
        test.ExpectedDiagnostics.Add(MutableShape(0, "Value"));

        await test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_public_field_becomes_readonly()
    {
        var test = new CodeFixFixture<ManagedStateMutationAnalyzer, MutableShapeCodeFixProvider>
        {
            TestCode = CodeFixTestHost.Attributes + """

                [Statesman.ManagedState]
                public sealed class Counter
                {
                    public int {|#0:Value|};
                }
                """,
            FixedCode = CodeFixTestHost.Attributes + """

                [Statesman.ManagedState]
                public sealed class Counter
                {
                    public readonly int Value;
                }
                """,
            ReferenceAssemblies = CodeFixTestHost.Modern,
        };
        test.ExpectedDiagnostics.Add(MutableShape(0, "Value"));

        await test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_field_assigned_only_in_the_declaring_constructor_becomes_readonly_and_still_compiles()
    {
        var test = new CodeFixFixture<ManagedStateMutationAnalyzer, MutableShapeCodeFixProvider>
        {
            TestCode = CodeFixTestHost.Attributes + """

                [Statesman.ManagedState]
                public sealed class Counter
                {
                    public int {|#0:Value|};

                    public Counter(int value) => Value = value;
                }
                """,
            FixedCode = CodeFixTestHost.Attributes + """

                [Statesman.ManagedState]
                public sealed class Counter
                {
                    public readonly int Value;

                    public Counter(int value) => Value = value;
                }
                """,
            ReferenceAssemblies = CodeFixTestHost.Modern,
        };
        test.ExpectedDiagnostics.Add(MutableShape(0, "Value"));

        await test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fix_all_rewrites_every_reported_property_in_one_pass()
    {
        var test = new CodeFixFixture<ManagedStateMutationAnalyzer, MutableShapeCodeFixProvider>
        {
            TestCode = CodeFixTestHost.Attributes + """

                [Statesman.ManagedState]
                public sealed class Counter
                {
                    public int {|#0:First|} { get; set; }
                    public string {|#1:Second|} { get; set; } = "";
                }
                """,
            FixedCode = CodeFixTestHost.Attributes + """

                [Statesman.ManagedState]
                public sealed class Counter
                {
                    public int First { get; init; }
                    public string Second { get; init; } = "";
                }
                """,
            ReferenceAssemblies = CodeFixTestHost.Modern,
            NumberOfFixAllIterations = 1,
        };
        test.ExpectedDiagnostics.Add(MutableShape(0, "First"));
        test.ExpectedDiagnostics.Add(MutableShape(1, "Second"));

        await test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_setter_with_a_body_is_reported_and_deliberately_not_fixed()
    {
        const string source = """

            [Statesman.ManagedState]
            public sealed class Counter
            {
                private int _value;

                public int {|#0:Value|}
                {
                    get => _value;
                    set => {|#1:_value|} = value;
                }
            }
            """;
        var test = new CodeFixFixture<ManagedStateMutationAnalyzer, MutableShapeCodeFixProvider>
        {
            TestCode = CodeFixTestHost.Attributes + source,
            FixedCode = CodeFixTestHost.Attributes + source,
            ReferenceAssemblies = CodeFixTestHost.Modern,
        };
        test.ExpectedDiagnostics.Add(MutableShape(0, "Value"));
        test.ExpectedDiagnostics.Add(DirectMutation(1, "_value", "Counter"));

        await test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_multi_variable_field_declaration_is_reported_and_deliberately_not_fixed()
    {
        const string source = """

            [Statesman.ManagedState]
            public sealed class Counter
            {
                public int {|#0:First|}, {|#1:Second|};
            }
            """;
        var test = new CodeFixFixture<ManagedStateMutationAnalyzer, MutableShapeCodeFixProvider>
        {
            TestCode = CodeFixTestHost.Attributes + source,
            FixedCode = CodeFixTestHost.Attributes + source,
            ReferenceAssemblies = CodeFixTestHost.Modern,
        };
        test.ExpectedDiagnostics.Add(MutableShape(0, "First"));
        test.ExpectedDiagnostics.Add(MutableShape(1, "Second"));

        await test.RunAsync(TestContext.Current.CancellationToken);
    }
}
