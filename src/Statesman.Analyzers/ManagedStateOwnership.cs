using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Statesman.Analyzers;

/// <summary>
/// The one place that answers "is this expression owned by managed state, and which managed type owns
/// it?". Both <see cref="ManagedStateMutationAnalyzer"/> (STM001, STM002) and
/// <see cref="ManagedStateCollectionMutationAnalyzer"/> (STM004) call it, so the two rules cannot
/// drift apart on what ownership means.
/// </summary>
internal static class ManagedStateOwnership
{
    public const string ManagedStateAttribute = "Statesman.ManagedStateAttribute";
    public const string BoundaryAttribute = "Statesman.StateMutationBoundaryAttribute";
    public const string IgnoreAttribute = "Statesman.StateMutationAnalysisIgnoreAttribute";

    /// <summary>
    /// Walks the receiver chain outermost-first and returns the <c>[ManagedState]</c> type that owns
    /// the first property or field it finds. One hop or twenty: <c>state.Child.Items</c> answers with
    /// whichever link is declared on a managed type, so a plain nested class inside managed state is
    /// covered, and a managed type reached through an unmanaged one is too. There is no data flow and
    /// no alias tracking, so <c>var list = state.Items; list.Add(x)</c> is deliberately out of scope.
    /// </summary>
    /// <param name="context">The syntax-node context whose semantic model resolves each link.</param>
    /// <param name="receiver">The expression to walk, innermost node first.</param>
    /// <returns>The owning managed type, or <see langword="null"/> when no link is owned by one.</returns>
    public static INamedTypeSymbol? ResolveManagedStateOwner(
        SyntaxNodeAnalysisContext context,
        ExpressionSyntax receiver)
    {
        for (ExpressionSyntax? current = receiver; current is not null;)
        {
            ISymbol? symbol = context.SemanticModel.GetSymbolInfo(current, context.CancellationToken).Symbol;
            INamedTypeSymbol? owner = symbol switch
            {
                IPropertySymbol property => property.ContainingType,
                IFieldSymbol field => field.ContainingType,
                _ => null,
            };
            // An escape hatch silences what it marks AND everything reached through it. An ignored
            // link therefore STOPS the walk rather than letting it continue outward and re-attribute
            // the same write to an enclosing managed type: a consumer who suppressed a nested
            // [ManagedState] type, or one member of a managed owner, made a decision about
            // everything behind it. ROADMAP 0.3 Phase 20 final-review finding I2, option (a).
            if (symbol is not null && HasAttribute(symbol, IgnoreAttribute))
            {
                return null;
            }

            if (owner is not null && HasAttribute(owner, ManagedStateAttribute))
            {
                return HasAttribute(owner, IgnoreAttribute) ? null : owner;
            }

            current = current switch
            {
                MemberAccessExpressionSyntax access => access.Expression,
                ElementAccessExpressionSyntax element => element.Expression,
                ConditionalAccessExpressionSyntax conditional => conditional.Expression,
                ParenthesizedExpressionSyntax parenthesized => parenthesized.Expression,
                // A null-conditional chain puts a member BINDING where the walk expects a member
                // access: in `s.Plain?.Items.Add(1)` the receiver `.Items` is a
                // MemberBindingExpression whose own receiver is the enclosing conditional access's
                // Expression, not a child of the binding. Without this arm the walk stopped one link
                // short and every null-conditional receiver was a false negative. Finding I4.
                MemberBindingExpressionSyntax binding => ConditionalReceiver(binding),
                // A conversion is not a link in the chain, it is a lens on one. Without these three
                // arms the walk stopped at the conversion and every cast receiver was a false
                // negative for BOTH rules: ((List<int>)s.Coll).Add(1) and ((PlainBag)s.Plain).Value = 1
                // were silent. ROADMAP 0.3 Phase 21, research finding D2.
                CastExpressionSyntax cast => cast.Expression,
                BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AsExpression) => binary.Left,
                PostfixUnaryExpressionSyntax suppression
                    when suppression.IsKind(SyntaxKind.SuppressNullableWarningExpression) =>
                    suppression.Operand,
                _ => null,
            };
        }

        return null;
    }

    /// <summary>
    /// The receiver a null-conditional member binding hangs off: the <c>Expression</c> of the
    /// nearest enclosing <see cref="ConditionalAccessExpressionSyntax"/> that holds the binding in
    /// its <c>WhenNotNull</c> half. The <c>WhenNotNull</c> test is what makes a chained
    /// <c>a?.b?.c</c> walk outward to <c>a</c> instead of returning the binding it started from:
    /// <c>.b</c> is the inner conditional's own <c>Expression</c>, so only the outer conditional
    /// answers for it.
    /// </summary>
    /// <param name="binding">The member binding to find a receiver for.</param>
    /// <returns>The receiver expression, or <see langword="null"/> when there is no enclosing conditional access.</returns>
    private static ExpressionSyntax? ConditionalReceiver(MemberBindingExpressionSyntax binding)
    {
        for (SyntaxNode? current = binding.Parent; current is not null; current = current.Parent)
        {
            if (current is ConditionalAccessExpressionSyntax conditional &&
                conditional.WhenNotNull.Span.Contains(binding.Span))
            {
                return conditional.Expression;
            }
        }

        return null;
    }

    /// <summary>
    /// True when the enclosing symbol is a declared mutation boundary, is ignored, or is the managed
    /// type's own constructor. Note that it is the constructor specifically, not any other member of
    /// the managed type.
    /// </summary>
    /// <param name="symbol">The symbol enclosing the mutation site.</param>
    /// <param name="stateType">The managed type the mutation reaches.</param>
    /// <returns><see langword="true"/> when the mutation is allowed here.</returns>
    public static bool IsAllowedBoundary(ISymbol? symbol, INamedTypeSymbol stateType)
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

    /// <summary>
    /// True when the expression sits inside an object, collection or <c>with</c> initializer, which
    /// constructs a value rather than mutating a stored one.
    /// </summary>
    /// <param name="target">The expression to classify.</param>
    /// <returns><see langword="true"/> when the expression is part of an initializer.</returns>
    public static bool IsInitialization(ExpressionSyntax target)
    {
        for (SyntaxNode? current = target.Parent; current is not null; current = current.Parent)
        {
            if (current is InitializerExpressionSyntax initializer &&
                initializer.Parent is ObjectCreationExpressionSyntax
                    or ImplicitObjectCreationExpressionSyntax
                    or WithExpressionSyntax)
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

    /// <summary>Whether a symbol carries the named attribute.</summary>
    /// <param name="symbol">The symbol to inspect.</param>
    /// <param name="metadataName">The attribute's fully qualified display name.</param>
    /// <returns><see langword="true"/> when the attribute is present.</returns>
    public static bool HasAttribute(ISymbol symbol, string metadataName) =>
        symbol.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == metadataName);
}
