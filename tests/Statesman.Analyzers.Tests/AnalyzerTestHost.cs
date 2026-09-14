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
    /// The compiler's own errors for one source. A fixture that does not compile silences every
    /// analyzer, so an empty analyzer-diagnostic list only means something once this is empty too.
    /// </summary>
    public static ImmutableArray<Diagnostic> CompileErrors(string source) =>
        Compile(source)
            .GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();

    private static CSharpCompilation Compile(string source)
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
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
