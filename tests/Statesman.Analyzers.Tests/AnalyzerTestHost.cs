using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Statesman.Analyzers.Tests;

internal static class AnalyzerTestHost
{
    public static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(
        string source,
        params DiagnosticAnalyzer[] analyzers)
    {
        CompilationWithAnalyzers withAnalyzers = Compile(source).WithAnalyzers(analyzers.ToImmutableArray());
        return await withAnalyzers.GetAnalyzerDiagnosticsAsync();
    }

    /// <summary>
    /// The same analysis over a top-level-statements program. The output kind is the whole
    /// difference: a library compilation rejects top-level statements with CS8805, so a fixture
    /// built by <c>ManagedStateFixture.TopLevelConsumer</c> has to come through here.
    /// </summary>
    /// <param name="source">A complete top-level-statements source.</param>
    /// <param name="analyzers">The analyzers to run.</param>
    /// <returns>Every analyzer diagnostic the source produces.</returns>
    public static async Task<ImmutableArray<Diagnostic>> AnalyzeTopLevelAsync(
        string source,
        params DiagnosticAnalyzer[] analyzers)
    {
        CompilationWithAnalyzers withAnalyzers = Compile(source, OutputKind.ConsoleApplication)
            .WithAnalyzers(analyzers.ToImmutableArray());
        return await withAnalyzers.GetAnalyzerDiagnosticsAsync();
    }

    /// <summary>
    /// The compiler's own errors for one source. A fixture that does not compile silences every
    /// analyzer, so an empty analyzer-diagnostic list only means something once this is empty too.
    /// </summary>
    public static ImmutableArray<Diagnostic> CompileErrors(string source) =>
        Compile(source)
            .GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();

    /// <summary>The compiler's own errors for one top-level-statements source.</summary>
    /// <param name="source">A complete top-level-statements source.</param>
    /// <returns>Every compiler error the source produces.</returns>
    public static ImmutableArray<Diagnostic> CompileErrorsTopLevel(string source) =>
        Compile(source, OutputKind.ConsoleApplication)
            .GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();

    private static CSharpCompilation Compile(
        string source,
        OutputKind outputKind = OutputKind.DynamicallyLinkedLibrary)
    {
        SyntaxTree syntax = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Preview));
        string[] trusted = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
            ?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            ?? Array.Empty<string>();
        MetadataReference[] references = trusted
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToArray();
        return CSharpCompilation.Create(
            "AnalyzerTests",
            new[] { syntax },
            references,
            new CSharpCompilationOptions(outputKind));
    }
}
