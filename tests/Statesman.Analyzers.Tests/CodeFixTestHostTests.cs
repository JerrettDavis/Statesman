using Microsoft.CodeAnalysis.Testing;
using Statesman.Analyzers;

namespace Statesman.Analyzers.Tests;

public sealed class CodeFixTestHostTests
{
    [Fact]
    public async Task The_roslyn_testing_harness_runs_an_analyzer_under_xunit_v3()
    {
        var test = new AnalyzerFixture<ManagedStateMutationAnalyzer>
        {
            TestCode = CodeFixTestHost.Attributes + """

                [Statesman.ManagedState]
                public sealed class Counter
                {
                    public int {|#0:Value|} { get; set; }
                }
                """,
            ReferenceAssemblies = CodeFixTestHost.Modern,
        };
        test.ExpectedDiagnostics.Add(
            new DiagnosticResult("STM002", Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
                .WithLocation(0)
                .WithArguments("Value"));

        await test.RunAsync(TestContext.Current.CancellationToken);
    }
}
