using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Statesman.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ManagedStateMutationAnalyzer : DiagnosticAnalyzer
{
    private const string ManagedStateAttribute = "Statesman.ManagedStateAttribute";
    private const string BoundaryAttribute = "Statesman.StateMutationBoundaryAttribute";
    private const string IgnoreAttribute = "Statesman.StateMutationAnalysisIgnoreAttribute";

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
        if (IsInitialization(target))
        {
            return;
        }

        ISymbol? symbol = context.SemanticModel.GetSymbolInfo(target, context.CancellationToken).Symbol;
        INamedTypeSymbol? stateType = symbol switch
        {
            IPropertySymbol property => property.ContainingType,
            IFieldSymbol field => field.ContainingType,
            _ => null,
        };
        if (symbol is null || stateType is null || !HasAttribute(stateType, ManagedStateAttribute))
        {
            return;
        }

        if (HasAttribute(symbol, IgnoreAttribute) || HasAttribute(stateType, IgnoreAttribute))
        {
            return;
        }

        ISymbol? enclosing = context.SemanticModel.GetEnclosingSymbol(target.SpanStart, context.CancellationToken);
        if (IsAllowedBoundary(enclosing, stateType))
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
        if (!HasAttribute(type, ManagedStateAttribute) || HasAttribute(type, IgnoreAttribute))
        {
            return;
        }

        foreach (ISymbol member in type.GetMembers())
        {
            if (member.IsStatic || HasAttribute(member, IgnoreAttribute))
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

    private static bool IsAllowedBoundary(ISymbol? symbol, INamedTypeSymbol stateType)
    {
        for (ISymbol? current = symbol; current is not null; current = current.ContainingSymbol)
        {
            if (HasAttribute(current, BoundaryAttribute) || HasAttribute(current, IgnoreAttribute))
            {
                return true;
            }

            if (current is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor &&
                SymbolEqualityComparer.Default.Equals(constructor.ContainingType, stateType))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInitialization(ExpressionSyntax target)
    {
        for (SyntaxNode? current = target.Parent; current is not null; current = current.Parent)
        {
            if (current is InitializerExpressionSyntax initializer &&
                initializer.Parent is ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax or WithExpressionSyntax)
            {
                return true;
            }

            if (current is StatementSyntax or MemberDeclarationSyntax)
            {
                break;
            }
        }

        return false;
    }

    private static bool HasAttribute(ISymbol symbol, string metadataName) =>
        symbol.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == metadataName);
}
