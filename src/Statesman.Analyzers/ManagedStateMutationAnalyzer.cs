using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Statesman.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ManagedStateMutationAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(DiagnosticDescriptors.DirectMutation, DiagnosticDescriptors.MutableShape);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(
            AnalyzeAssignment,
            SyntaxKind.SimpleAssignmentExpression,
            SyntaxKind.AddAssignmentExpression,
            SyntaxKind.SubtractAssignmentExpression,
            SyntaxKind.MultiplyAssignmentExpression,
            SyntaxKind.DivideAssignmentExpression,
            SyntaxKind.ModuloAssignmentExpression,
            SyntaxKind.AndAssignmentExpression,
            SyntaxKind.ExclusiveOrAssignmentExpression,
            SyntaxKind.OrAssignmentExpression,
            SyntaxKind.LeftShiftAssignmentExpression,
            SyntaxKind.RightShiftAssignmentExpression,
            SyntaxKind.CoalesceAssignmentExpression);
        context.RegisterSyntaxNodeAction(
            AnalyzeIncrement,
            SyntaxKind.PreIncrementExpression,
            SyntaxKind.PreDecrementExpression,
            SyntaxKind.PostIncrementExpression,
            SyntaxKind.PostDecrementExpression);
        context.RegisterSymbolAction(AnalyzeShape, SymbolKind.NamedType);
    }

    private static void AnalyzeAssignment(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        AnalyzeMutation(context, assignment.Left);
    }

    private static void AnalyzeIncrement(SyntaxNodeAnalysisContext context)
    {
        var expression = (ExpressionSyntax)context.Node;
        ExpressionSyntax operand = expression switch
        {
            PrefixUnaryExpressionSyntax prefix => prefix.Operand,
            PostfixUnaryExpressionSyntax postfix => postfix.Operand,
            _ => expression,
        };
        AnalyzeMutation(context, operand);
    }

    private static void AnalyzeMutation(SyntaxNodeAnalysisContext context, ExpressionSyntax target)
    {
        if (ManagedStateOwnership.IsInitialization(target))
        {
            return;
        }

        ISymbol? symbol = context.SemanticModel.GetSymbolInfo(target, context.CancellationToken).Symbol;
        if (symbol is not (IPropertySymbol or IFieldSymbol))
        {
            return;
        }

        if (ManagedStateOwnership.HasAttribute(symbol, ManagedStateOwnership.IgnoreAttribute))
        {
            return;
        }

        // Ownership is the receiver-chain walk, not the immediately containing type. A plain class
        // held by managed state is still managed state, so `state.Plain.Value = 1` reports here for
        // the same reason `state.Plain.Items.Add(1)` reports STM004 — the two rules share one
        // definition of "owned by managed state". ROADMAP 0.3 Phase 20 addendum decision 105.
        INamedTypeSymbol? stateType = ManagedStateOwnership.ResolveManagedStateOwner(context, target);
        if (stateType is null)
        {
            return;
        }

        ISymbol? enclosing = context.SemanticModel.GetEnclosingSymbol(target.SpanStart, context.CancellationToken);
        if (ManagedStateOwnership.IsAllowedBoundary(enclosing, stateType))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.DirectMutation,
            target.GetLocation(),
            symbol.Name,
            stateType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
    }

    private static void AnalyzeShape(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (!ManagedStateOwnership.HasAttribute(type, ManagedStateOwnership.ManagedStateAttribute) || ManagedStateOwnership.HasAttribute(type, ManagedStateOwnership.IgnoreAttribute))
        {
            return;
        }

        foreach (ISymbol member in type.GetMembers())
        {
            if (member.IsStatic || ManagedStateOwnership.HasAttribute(member, ManagedStateOwnership.IgnoreAttribute))
            {
                continue;
            }

            bool mutable = member switch
            {
                IFieldSymbol field => field.DeclaredAccessibility == Accessibility.Public && !field.IsReadOnly && !field.IsConst,
                IPropertySymbol property =>
                    property.SetMethod is { DeclaredAccessibility: Accessibility.Public, IsInitOnly: false },
                _ => false,
            };
            if (!mutable)
            {
                continue;
            }

            Location? location = member.Locations.FirstOrDefault(value => value.IsInSource);
            if (location is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.MutableShape,
                    location,
                    member.Name));
            }
        }
    }
}
