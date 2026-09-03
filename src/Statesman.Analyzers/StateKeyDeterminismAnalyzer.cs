using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Statesman.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StateKeyDeterminismAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(DiagnosticDescriptors.DynamicStateKey);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method ||
            method.Name != "Define" ||
            method.ContainingType.ToDisplayString() != "Statesman.StateKey" ||
            invocation.ArgumentList.Arguments.Count == 0)
        {
            return;
        }

        ExpressionSyntax argument = invocation.ArgumentList.Arguments[0].Expression;
        Optional<object?> constant = context.SemanticModel.GetConstantValue(argument, context.CancellationToken);
        if (!constant.HasValue || constant.Value is not string)
        {
            context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.DynamicStateKey, argument.GetLocation()));
        }
    }
}
