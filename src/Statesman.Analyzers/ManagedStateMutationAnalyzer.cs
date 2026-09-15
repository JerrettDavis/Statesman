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
        AnalyzeTarget(context, assignment.Left);
    }

    /// <summary>
    /// Reports every element a deconstructing assignment writes, rather than the tuple as a whole.
    /// A deconstruction's left-hand side is a <see cref="TupleExpressionSyntax"/> whose
    /// <c>GetSymbolInfo</c> is neither a property nor a field, so handing it straight to
    /// <c>AnalyzeMutation</c> silenced every write it performs, and
    /// <c>(state.Scalar, _) = (1, 0)</c> went unreported while <c>state.Scalar = 1</c> on the same
    /// member reported. Each element is analysed on its own, so a two-element deconstruction into two
    /// managed members reports twice. Parentheses are stepped through for the same reason the
    /// ownership walk steps through them: they change nothing about which object is written. Every
    /// other shape falls through unchanged, which is what keeps a declaration form such as
    /// <c>var (a, b) = (1, 2)</c> or <c>(int c, int d) = (3, 4)</c> silent: those write locals, whose
    /// symbols this rule's own property-or-field test already rejects.
    /// </summary>
    /// <param name="context">The syntax-node context whose semantic model resolves the target.</param>
    /// <param name="target">The assignment target to report on, or to decompose first.</param>
    private static void AnalyzeTarget(SyntaxNodeAnalysisContext context, ExpressionSyntax target)
    {
        switch (target)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                AnalyzeTarget(context, parenthesized.Expression);
                return;

            case TupleExpressionSyntax tuple:
                foreach (ArgumentSyntax argument in tuple.Arguments)
                {
                    AnalyzeTarget(context, argument.Expression);
                }

                return;

            default:
                AnalyzeMutation(context, target);
                return;
        }
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

        // An indexer assignment on a collection reached through managed state is STM004's shape, not
        // this rule's. `state.Map["k"] = 1` resolves to the Dictionary<,> indexer, whose own type is
        // not managed state, so the receiver-chain walk would find `Map` and report a second
        // diagnostic on the same span naming a member called `this[]` — which defeats the documented
        // reason STM004 is a separate id at all. A managed type's OWN indexer setter still reports
        // here, because no other rule covers it. ROADMAP 0.3 Phase 20 final-review finding I1.
        if (symbol is IPropertySymbol { IsIndexer: true } &&
            !ManagedStateOwnership.HasAttribute(
                symbol.ContainingType,
                ManagedStateOwnership.ManagedStateAttribute))
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
