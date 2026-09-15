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
    private const string NonGenericCollectionInterface = "ICollection";
    private const string ImmutableNamespace = "System.Collections.Immutable";

    /// <summary>
    /// Names that mutate a collection in place. Membership alone never fires the rule: the declaring
    /// type must also implement <c>ICollection&lt;T&gt;</c> or the non-generic
    /// <c>System.Collections.ICollection</c>, and must not be an immutable collection, whose
    /// same-named members return a new collection instead of mutating the receiver. The type test is
    /// what makes ambiguous names safe: <c>Take</c> is also <c>Enumerable.Take</c>, whose declaring
    /// type is <c>System.Linq.Enumerable</c> and is no collection at all. <c>GetOrAdd</c> mutates
    /// only conditionally and is here anyway, because a write attempt against managed state is what
    /// this rule exists to name.
    /// </summary>
    private static readonly ImmutableHashSet<string> Mutators = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "Add",
        "AddAfter",
        "AddBefore",
        "AddFirst",
        "AddLast",
        "AddOrUpdate",
        "AddRange",
        "Clear",
        "CompleteAdding",
        "Dequeue",
        "Enqueue",
        "ExceptWith",
        "GetOrAdd",
        "Insert",
        "InsertRange",
        "IntersectWith",
        "Move",
        "Pop",
        "Push",
        "PushRange",
        "Remove",
        "RemoveAll",
        "RemoveAt",
        "RemoveFirst",
        "RemoveLast",
        "RemoveRange",
        "RemoveWhere",
        "Replace",
        "Reverse",
        "Set",
        "SetAll",
        "Sort",
        "SymmetricExceptWith",
        "Take",
        "TryAdd",
        "TryDequeue",
        "TryPop",
        "TryPopRange",
        "TryRemove",
        "TryTake",
        "TryUpdate",
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

        Report(context, access.Expression, DescribeCallee(invocation));
    }

    /// <summary>
    /// The invocation's callee as written, argument list removed. Taking the OUTERMOST enclosing
    /// conditional access is what keeps a null-conditional receiver in the message: for
    /// <c>s?.Items.Add(1)</c> the invocation's own expression is the bare binding <c>.Items.Add</c>,
    /// so building the description from it rendered a message that began with a dot. Finding I4.
    /// </summary>
    private static string DescribeCallee(InvocationExpressionSyntax invocation)
    {
        SyntaxNode outermost = invocation;
        while (outermost.Parent is ConditionalAccessExpressionSyntax conditional &&
               conditional.WhenNotNull.Span.Contains(invocation.Span))
        {
            outermost = conditional;
        }

        string text = outermost.ToString();
        string arguments = invocation.ArgumentList.ToString();
        return text.EndsWith(arguments, StringComparison.Ordinal)
            ? text.Substring(0, text.Length - arguments.Length)
            : text;
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

        // No IsInitialization guard here, deliberately. It was unreachable: a collection
        // initializer's `Items = { 1, 2 }` form emits no InvocationExpressionSyntax for
        // AnalyzeInvocation to see, and an element initializer's `[0] = 1` is an
        // ImplicitElementAccess rather than the ElementAccessExpressionSyntax AnalyzeElementTarget
        // requires — so neither entry point can reach an initializer. STM001's copy of the guard is
        // live and stays. ROADMAP 0.3 Phase 20 final-review finding I3.
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
    /// True when <paramref name="type"/> mutates in place: it implements <c>ICollection&lt;T&gt;</c>
    /// or the non-generic <c>System.Collections.ICollection</c> (or is one of them), and is not one of
    /// the immutable collections, whose <c>Add</c> and <c>Remove</c> return a new collection and leave
    /// the receiver alone.
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

    // Two operands, because neither implies the other. The non-generic one admits Queue<T>,
    // Stack<T>, every IProducerConsumerCollection<T>, BlockingCollection<T> and every legacy
    // collection, since IList, IDictionary and IProducerConsumerCollection<T> all extend
    // System.Collections.ICollection. The generic one stays because ISet<T>, IList<T>,
    // IDictionary<,> and ICollection<T> do NOT extend it, so a member typed as one of those
    // interfaces is reached by nothing else.
    private static bool IsCollectionInterface(INamedTypeSymbol type) =>
        (type.OriginalDefinition.MetadataName == CollectionInterface &&
            type.OriginalDefinition.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic") ||
        (type.OriginalDefinition.MetadataName == NonGenericCollectionInterface &&
            type.OriginalDefinition.ContainingNamespace?.ToDisplayString() == "System.Collections");
}
