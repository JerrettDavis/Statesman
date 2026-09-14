using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Statesman.CodeFixes;

/// <summary>
/// Turns a publicly settable auto-property on a <c>[ManagedState]</c> type into an init-only
/// property, and a public non-readonly field into a readonly field. Those are the two shapes STM002
/// reports; a setter with a body is deliberately left alone, because rewriting <c>set</c> to
/// <c>init</c> on a body changes when that body may run rather than only who may call it.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MutableShapeCodeFixProvider))]
[Shared]
public sealed class MutableShapeCodeFixProvider : CodeFixProvider
{
    private const string MakeInitOnlyTitle = "Make this property init-only";
    private const string MakeReadOnlyTitle = "Make this field readonly";

    /// <summary>STM002, publicly mutable managed state shape.</summary>
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create("STM002");

    /// <summary>
    /// The batch fixer, which groups actions by <c>equivalenceKey</c>. A single shared key per action
    /// kind is what lets every property on a type be fixed in one pass; RS1016 makes overriding this
    /// mandatory for any provider that registers a fix at all.
    /// </summary>
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <summary>Registers one action per reported member whose shape this provider can rewrite.</summary>
    /// <param name="context">The diagnostics and document Roslyn is offering a fix for.</param>
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        SyntaxNode? root = await context.Document
            .GetSyntaxRootAsync(context.CancellationToken)
            .ConfigureAwait(false);
        if (root is null)
        {
            return;
        }

        foreach (Diagnostic diagnostic in context.Diagnostics)
        {
            SyntaxNode node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);

            if (node.FirstAncestorOrSelf<PropertyDeclarationSyntax>() is { } property &&
                TryGetAutoSetter(property, out AccessorDeclarationSyntax? setter))
            {
                AccessorDeclarationSyntax target = setter!;
                context.RegisterCodeFix(
                    CodeAction.Create(
                        MakeInitOnlyTitle,
                        token => MakeInitOnlyAsync(context.Document, target, token),
                        equivalenceKey: MakeInitOnlyTitle),
                    diagnostic);
                continue;
            }

            if (node.FirstAncestorOrSelf<VariableDeclaratorSyntax>()?
                    .FirstAncestorOrSelf<FieldDeclarationSyntax>() is { } field &&
                field.Declaration.Variables.Count == 1 &&
                !field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword) &&
                !field.Modifiers.Any(SyntaxKind.ConstKeyword))
            {
                context.RegisterCodeFix(
                    CodeAction.Create(
                        MakeReadOnlyTitle,
                        token => MakeReadOnlyAsync(context.Document, field, token),
                        equivalenceKey: MakeReadOnlyTitle),
                    diagnostic);
            }
        }
    }

    private static bool TryGetAutoSetter(
        PropertyDeclarationSyntax property,
        out AccessorDeclarationSyntax? setter)
    {
        setter = property.AccessorList?.Accessors
            .FirstOrDefault(accessor => accessor.IsKind(SyntaxKind.SetAccessorDeclaration));

        // An auto-property setter has neither a block body nor an expression body. A setter with a
        // body may validate, raise a notification, or write through to something else; swapping the
        // keyword would silently change when that body may run, so it is not fixed.
        return setter is { Body: null, ExpressionBody: null };
    }

    private static async Task<Document> MakeInitOnlyAsync(
        Document document,
        AccessorDeclarationSyntax setter,
        CancellationToken cancellationToken)
    {
        SyntaxNode? root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return document;
        }

        AccessorDeclarationSyntax replacement = SyntaxFactory
            .AccessorDeclaration(SyntaxKind.InitAccessorDeclaration)
            .WithModifiers(setter.Modifiers)
            .WithSemicolonToken(setter.SemicolonToken)
            .WithTriviaFrom(setter);
        return document.WithSyntaxRoot(root.ReplaceNode(setter, replacement));
    }

    private static async Task<Document> MakeReadOnlyAsync(
        Document document,
        FieldDeclarationSyntax field,
        CancellationToken cancellationToken)
    {
        SyntaxNode? root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return document;
        }

        SyntaxToken keyword = SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)
            .WithTrailingTrivia(SyntaxFactory.Space);
        FieldDeclarationSyntax replacement = field.WithModifiers(field.Modifiers.Add(keyword));
        return document.WithSyntaxRoot(root.ReplaceNode(field, replacement));
    }
}
