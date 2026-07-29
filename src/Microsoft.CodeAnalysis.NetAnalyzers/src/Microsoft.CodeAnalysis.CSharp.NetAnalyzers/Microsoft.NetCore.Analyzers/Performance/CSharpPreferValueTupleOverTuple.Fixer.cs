// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Analyzer.Utilities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Text;
using Microsoft.NetCore.Analyzers;
using Microsoft.NetCore.Analyzers.Performance;

namespace Microsoft.NetCore.CSharp.Analyzers.Performance
{
    /// <summary>
    /// CA1880: Prefer 'ValueTuple' over 'Tuple'.
    /// Rewrites 'new Tuple&lt;...&gt;(...)' and 'Tuple.Create(...)' callsites to their 'ValueTuple'
    /// equivalents. The generic type arguments are preserved, so the rewrite does not change the
    /// element types (unlike converting to C# tuple syntax, which would re-run type inference).
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp), Shared]
    public sealed class CSharpPreferValueTupleOverTupleFixer : CodeFixProvider
    {
        private const string ValueTupleName = "ValueTuple";

        public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create(PreferValueTupleOverTupleAnalyzer.RuleId);

        // The rule reports both fixable callsite allocations and non-fixable signature usages under one ID,
        // and reported allocations can nest ('Tuple.Create(1, Tuple.Create(2, 3))'). Fix-all therefore rewrites
        // inside-out through a single editor so an outer rewrite observes the inner ones, rather than merging
        // independent edits the way the batch fixer would.
        public override FixAllProvider GetFixAllProvider()
            => FixAllProvider.Create(async (fixAllContext, document, diagnostics) => await FixAllAsync(document, diagnostics, fixAllContext.CancellationToken).ConfigureAwait(false));

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetRequiredSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);

            // Only offer the fix for the callsite shapes it can rewrite; signature diagnostics share the rule ID
            // but have no fix, so registering unconditionally would surface a lightbulb that does nothing.
            if (GetTupleNameToReplace(root, context.Span) is null)
            {
                return;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: MicrosoftNetCoreAnalyzersResources.PreferValueTupleOverTupleCodeFixTitle,
                    createChangedDocument: cancellationToken => FixAllAsync(context.Document, ImmutableArray.Create(context.Diagnostics[0]), cancellationToken),
                    equivalenceKey: nameof(MicrosoftNetCoreAnalyzersResources.PreferValueTupleOverTupleCodeFixTitle)),
                context.Diagnostics[0]);
        }

        private static async Task<Document> FixAllAsync(Document document, ImmutableArray<Diagnostic> diagnostics, CancellationToken cancellationToken)
        {
            var editor = await DocumentEditor.CreateAsync(document, cancellationToken).ConfigureAwait(false);
            var root = editor.OriginalRoot;

            // Inner nodes come first so an enclosing rewrite is applied after the ones it contains.
            foreach (var diagnostic in diagnostics.OrderByDescending(diagnostic => diagnostic.Location.SourceSpan.Start))
            {
                if (GetTupleNameToReplace(root, diagnostic.Location.SourceSpan) is { } tupleName)
                {
                    editor.ReplaceNode(tupleName, (currentNode, _) => WithValueTupleIdentifier((SimpleNameSyntax)currentNode));
                }
            }

            return editor.GetChangedDocument();
        }

        private static SimpleNameSyntax? GetTupleNameToReplace(SyntaxNode root, TextSpan span)
        {
            var node = root.FindNode(span, getInnermostNodeForTie: true);

            return node switch
            {
                ObjectCreationExpressionSyntax objectCreation => GetTupleName(objectCreation.Type),
                InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax memberAccess } => GetTupleName(memberAccess.Expression),
                _ => null,
            };
        }

        // The 'Tuple' name can appear as a bare identifier ('Tuple'), the right-hand side of a
        // qualified name ('System.Tuple'), an aliased name ('global::System.Tuple'), or a member
        // access expression when used as a receiver. In every case the simple name to rewrite is the
        // final segment.
        private static SimpleNameSyntax? GetTupleName(SyntaxNode node) => node switch
        {
            GenericNameSyntax genericName => genericName,
            IdentifierNameSyntax identifierName => identifierName,
            QualifiedNameSyntax qualifiedName => qualifiedName.Right,
            AliasQualifiedNameSyntax aliasQualifiedName => aliasQualifiedName.Name,
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name,
            _ => null,
        };

        private static SimpleNameSyntax WithValueTupleIdentifier(SimpleNameSyntax tupleName)
        {
            var newIdentifier = SyntaxFactory.Identifier(ValueTupleName).WithTriviaFrom(tupleName.Identifier);

            return tupleName is GenericNameSyntax genericName
                ? genericName.WithIdentifier(newIdentifier)
                : SyntaxFactory.IdentifierName(newIdentifier);
        }
    }
}
