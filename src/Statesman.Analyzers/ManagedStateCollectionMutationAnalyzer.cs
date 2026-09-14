using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Statesman.Analyzers;

/// <summary>
/// STM004: a call to a known mutating member, or an element assignment, whose receiver resolves
/// through a property or field of a <c>[ManagedState]</c> type. Ownership is answered by
/// <see cref="ManagedStateOwnership.ResolveManagedStateOwner"/>, the same walk STM001 uses, so the two
/// rules agree on what "owned by managed state" means. There is no data flow and no alias tracking.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ManagedStateCollectionMutationAnalyzer : DiagnosticAnalyzer
{
    private const string CollectionInterface = "ICollection`1";
    private const string ImmutableNamespace = "System.Collections.Immutable";

    /// <summary>
    /// Names that mutate a collection in place. Membership alone never fires the rule: the declaring
    /// type must also implement <c>ICollection&lt;T&gt;</c> and must not be an immutable collection,
    /// whose same-named members return a new collection instead of mutating the receiver.
    /// </summary>
    private static readonly ImmutableHashSet<string> Mutators = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "Add",
        "AddRange",
        "Clear",
        "ExceptWith",
        "Insert",
        "InsertRange",
        "IntersectWith",
        "Remove",
        "RemoveAll",
        "RemoveAt",
        "RemoveRange",
        "Reverse",
        "Sort",
        "SymmetricExceptWith",
        "TryAdd",
        "UnionWith");

    /// <summary>STM004.</summary>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(DiagnosticDescriptors.CollectionMutation);

    /// <summary>Registers the invocation, assignment and increment actions the rule needs.</summary>
    /// <param name="context">The analysis context.</param>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(
            AnalyzeElementAssignment,
            SyntaxKind.SimpleAssignmentExpression,
            SyntaxKind.AddAssignmentExpression,
            SyntaxKind.SubtractAssignmentExpression,
            SyntaxKind.CoalesceAssignmentExpression);
        context.RegisterSyntaxNodeAction(
            AnalyzeElementIncrement,
            SyntaxKind.PreIncrementExpression,
            SyntaxKind.PreDecrementExpression,
            SyntaxKind.PostIncrementExpression,
            SyntaxKind.PostDecrementExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax access)
        {
            return;
        }

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol
            is not IMethodSymbol method)
        {
            return;
        }

        if (!Mutators.Contains(method.Name) || !IsInPlaceCollectionMember(method.ContainingType))
        {
            return;
        }

        Report(context, access.Expression, $"{access.Expression}.{method.Name}");
    }

    private static void AnalyzeElementAssignment(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        AnalyzeElementTarget(context, assignment.Left);
    }

    private static void AnalyzeElementIncrement(SyntaxNodeAnalysisContext context)
    {
        ExpressionSyntax operand = context.Node switch
        {
            PrefixUnaryExpressionSyntax prefix => prefix.Operand,
            PostfixUnaryExpressionSyntax postfix => postfix.Operand,
            _ => (ExpressionSyntax)context.Node,
        };
        AnalyzeElementTarget(context, operand);
    }

    private static void AnalyzeElementTarget(SyntaxNodeAnalysisContext context, ExpressionSyntax target)
    {
        if (target is not ElementAccessExpressionSyntax element)
        {
            return;
        }

        ISymbol? indexer = context.SemanticModel.GetSymbolInfo(element, context.CancellationToken).Symbol;
        bool mutable = indexer switch
        {
            // An array element assignment resolves to no symbol at all, so the receiver's type is
            // what answers for it.
            null => context.SemanticModel.GetTypeInfo(element.Expression, context.CancellationToken).Type
                is IArrayTypeSymbol,
            IPropertySymbol { IsIndexer: true } property =>
                property.SetMethod is not null && IsInPlaceCollectionMember(property.ContainingType),
            _ => false,
        };

        if (!mutable)
        {
            return;
        }

        Report(context, element.Expression, $"{element.Expression}[...]");
    }

    private static void Report(
        SyntaxNodeAnalysisContext context,
        ExpressionSyntax receiver,
        string description)
    {
        INamedTypeSymbol? stateType = ManagedStateOwnership.ResolveManagedStateOwner(context, receiver);
        if (stateType is null)
        {
            return;
        }

        if (ManagedStateOwnership.IsInitialization(receiver))
        {
            return;
        }

        ISymbol? enclosing = context.SemanticModel.GetEnclosingSymbol(receiver.SpanStart, context.CancellationToken);
        if (ManagedStateOwnership.IsAllowedBoundary(enclosing, stateType))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.CollectionMutation,
            receiver.GetLocation(),
            description,
            stateType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
    }

    /// <summary>
    /// True when <paramref name="type"/> mutates in place: it implements
    /// <c>ICollection&lt;T&gt;</c> (or is that interface) and is not one of the immutable collections,
    /// whose <c>Add</c> and <c>Remove</c> return a new collection and leave the receiver alone.
    /// </summary>
    private static bool IsInPlaceCollectionMember(INamedTypeSymbol? type)
    {
        if (type is null)
        {
            return false;
        }

        // Only the TOP-LEVEL immutable collections are excluded. A nested type in that namespace —
        // ImmutableList&lt;T&gt;.Builder above all — mutates in place and must fire. The ContainingType
        // test is what distinguishes them: ContainingNamespace for a nested type is the namespace of
        // its containing type, so a namespace test alone would exclude the builders too.
        if (type.ContainingType is null &&
            type.ContainingNamespace?.ToDisplayString() is ImmutableNamespace)
        {
            return false;
        }

        return IsCollectionInterface(type) || type.AllInterfaces.Any(IsCollectionInterface);
    }

    private static bool IsCollectionInterface(INamedTypeSymbol type) =>
        type.OriginalDefinition.MetadataName == CollectionInterface &&
        type.OriginalDefinition.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic";
}
