using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace Statesman.Analyzers.Tests;

/// <summary>
/// Shared constants for the Roslyn testing harness, which is bound to <c>DefaultVerifier</c> rather
/// than to a test-framework flavour. The <c>.XUnit</c> flavour of the testing packages cannot be used
/// here: it depends on xunit.assert 2.3.0, which collides with this repository's xunit.v3.assert
/// 4.0.0 (CS0433 on every <c>Assert</c> call site), and its <c>XUnitVerifier</c> is
/// <c>[Obsolete]</c>, which <c>TreatWarningsAsErrors</c> turns into a build error.
/// </summary>
internal static class CodeFixTestHost
{
    /// <summary>
    /// The reference assemblies every fact whose fixed state uses <c>init</c> must run against. The
    /// harness defaults to <c>netcoreapp3.1</c>, which has no
    /// <c>System.Runtime.CompilerServices.IsExternalInit</c>, so an <c>init</c> accessor in the fixed
    /// document fails with CS0518 under the default.
    /// </summary>
    public static ReferenceAssemblies Modern => ReferenceAssemblies.Net.Net80;

    /// <summary>
    /// The Statesman attribute declarations every analyzer fact needs in its own source, because the
    /// harness compiles a bare source string that does not reference the Statesman assemblies.
    /// </summary>
    public const string Attributes = """
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
        """;
}

/// <summary>
/// A provider that offers no fix for anything. It exists so an analyzer-only fact can still use the
/// Roslyn harness and its <c>{|#0:...|}</c> location markup: the pinned
/// <c>Microsoft.CodeAnalysis.CSharp.CodeFix.Testing</c> package supplies
/// <c>CSharpCodeFixTest&lt;,,&gt;</c> but no C# flavour of the analyzer-only test, which lives in a
/// separate package this repository deliberately does not pin. A <c>CSharpCodeFixTest</c> whose
/// provider fixes nothing and whose fixed source equals its test source is exactly an analyzer test.
/// </summary>
internal sealed class NoFixProvider : CodeFixProvider
{
    /// <summary>Nothing is fixable.</summary>
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray<string>.Empty;

    /// <summary>Fix-all is explicitly unsupported, which is what RS1016 asks a non-fixing provider for.</summary>
    public override FixAllProvider? GetFixAllProvider() => null;

    /// <summary>Registers nothing.</summary>
    /// <param name="context">The context whose diagnostics are deliberately ignored.</param>
    public override Task RegisterCodeFixesAsync(CodeFixContext context) => Task.CompletedTask;
}

/// <summary>
/// An analyzer-only test over one C# source, verified with <c>DefaultVerifier</c> and driven through
/// <see cref="NoFixProvider"/>.
/// </summary>
/// <typeparam name="TAnalyzer">The analyzer under test.</typeparam>
internal sealed class AnalyzerFixture<TAnalyzer>
    : CSharpCodeFixTest<TAnalyzer, NoFixProvider, DefaultVerifier>
    where TAnalyzer : DiagnosticAnalyzer, new();

/// <summary>
/// A code-fix test over one C# source: the analyzer runs, the provider fixes every diagnostic it
/// declares fixable, and the resulting document is compared against the expected fixed source.
/// </summary>
/// <typeparam name="TAnalyzer">The analyzer that produces the diagnostics.</typeparam>
/// <typeparam name="TCodeFix">The provider under test.</typeparam>
internal sealed class CodeFixFixture<TAnalyzer, TCodeFix>
    : CSharpCodeFixTest<TAnalyzer, TCodeFix, DefaultVerifier>
    where TAnalyzer : DiagnosticAnalyzer, new()
    where TCodeFix : CodeFixProvider, new();
