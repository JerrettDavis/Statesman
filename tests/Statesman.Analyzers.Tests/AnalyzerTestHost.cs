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
        SyntaxTree syntax = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Preview));
        string[] trusted = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
            ?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            ?? Array.Empty<string>();
        MetadataReference[] references = trusted
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToArray();
        CSharpCompilation compilation = CSharpCompilation.Create(
            "AnalyzerTests",
            new[] { syntax },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        CompilationWithAnalyzers withAnalyzers = compilation.WithAnalyzers(analyzers.ToImmutableArray());
        return await withAnalyzers.GetAnalyzerDiagnosticsAsync();
    }
}
