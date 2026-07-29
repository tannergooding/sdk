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
using Microsoft.CodeAnalysis.Operations;
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
            var node = root.FindNode(context.Span, getInnermostNodeForTie: true);

            // Signature diagnostics share the rule ID but have no callsite to rewrite, so registering the fix
            // for them would only surface a lightbulb that does nothing.
            if (GetTupleNameToReplace(node) is null)
            {
                return;
            }

            // Swapping only the callsite breaks the build when the allocation flows into a location that is
            // independently typed as the reference 'Tuple' (e.g. 'Tuple<int, string> t = Tuple.Create(...)').
            // Still report the diagnostic, but do not offer a fix that would not compile.
            var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            if (semanticModel is null || ConversionWouldBreakBuild(semanticModel, node, context.CancellationToken))
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
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var editor = await DocumentEditor.CreateAsync(document, cancellationToken).ConfigureAwait(false);
            var root = editor.OriginalRoot;

            // Inner nodes come first so an enclosing rewrite is applied after the ones it contains.
            foreach (var diagnostic in diagnostics.OrderByDescending(diagnostic => diagnostic.Location.SourceSpan.Start))
            {
                var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);

                if (GetTupleNameToReplace(node) is { } tupleName &&
                    semanticModel is not null &&
                    !ConversionWouldBreakBuild(semanticModel, node, cancellationToken))
                {
                    editor.ReplaceNode(tupleName, (currentNode, _) => WithValueTupleIdentifier((SimpleNameSyntax)currentNode));
                }
            }

            return editor.GetChangedDocument();
        }

        private static SimpleNameSyntax? GetTupleNameToReplace(SyntaxNode node) => node switch
        {
            ObjectCreationExpressionSyntax objectCreation => GetTupleName(objectCreation.Type),
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax memberAccess } => GetTupleName(memberAccess.Expression),
            _ => null,
        };

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

        // A 'ValueTuple' is convertible to 'object' and to every interface the reference 'Tuple' implements,
        // so the only target that stops compiling after the swap is one whose type is independently fixed to a
        // reference 'Tuple'. A 'var' local, an inferred method type parameter, or a bare expression statement
        // all adapt to the rewritten type and stay valid.
        private static bool ConversionWouldBreakBuild(SemanticModel semanticModel, SyntaxNode allocation, CancellationToken cancellationToken)
        {
            var expression = (ExpressionSyntax)allocation;
            while (expression.Parent is ParenthesizedExpressionSyntax parenthesized)
            {
                expression = parenthesized;
            }

            switch (expression.Parent)
            {
                case EqualsValueClauseSyntax equalsValue:
                    return InitializerTargetIsReferenceTuple(semanticModel, equalsValue, cancellationToken);

                case CastExpressionSyntax cast:
                    return IsReferenceTuple(semanticModel.GetTypeInfo(cast.Type, cancellationToken).Type, semanticModel.Compilation);

                case AssignmentExpressionSyntax assignment when assignment.Right == expression:
                    return IsReferenceTuple(semanticModel.GetTypeInfo(assignment.Left, cancellationToken).Type, semanticModel.Compilation);

                // The enclosing member's return type is fixed, so 'return'/'=>' cannot adapt.
                case ReturnStatementSyntax:
                case ArrowExpressionClauseSyntax:
                    return IsReferenceTuple(semanticModel.GetTypeInfo(expression, cancellationToken).ConvertedType, semanticModel.Compilation);

                case ArgumentSyntax argument:
                    return ArgumentConversionWouldBreak(semanticModel, argument, cancellationToken);

                default:
                    return false;
            }
        }

        private static bool InitializerTargetIsReferenceTuple(SemanticModel semanticModel, EqualsValueClauseSyntax equalsValue, CancellationToken cancellationToken)
        {
            TypeSyntax? targetType = equalsValue.Parent switch
            {
                VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } => declaration.Type,
                PropertyDeclarationSyntax property => property.Type,
                ParameterSyntax parameter => parameter.Type,
                _ => null,
            };

            // A 'var' declaration rebinds to the rewritten type; an explicit 'Tuple' declaration does not.
            if (targetType is null || targetType.IsVar)
            {
                return false;
            }

            return IsReferenceTuple(semanticModel.GetTypeInfo(targetType, cancellationToken).Type, semanticModel.Compilation);
        }

        private static bool ArgumentConversionWouldBreak(SemanticModel semanticModel, ArgumentSyntax argument, CancellationToken cancellationToken)
        {
            if (semanticModel.GetOperation(argument, cancellationToken) is not IArgumentOperation { Parameter: { } parameter })
            {
                return false;
            }

            // A parameter typed as a method type parameter that the call infers from its arguments adapts to a
            // rewritten argument, so the call still binds ('Tuple.Create(x, Tuple.Create(...))'). A constructor's
            // parameters and any explicitly specified method type arguments are fixed and cannot adapt.
            if (parameter.OriginalDefinition.Type is ITypeParameterSymbol { DeclaringMethod: not null } &&
                argument.FirstAncestorOrSelf<InvocationExpressionSyntax>()?.Expression is { } callee &&
                callee is not GenericNameSyntax and not MemberAccessExpressionSyntax { Name: GenericNameSyntax })
            {
                return false;
            }

            return IsReferenceTuple(parameter.Type, semanticModel.Compilation);
        }

        private static bool IsReferenceTuple(ITypeSymbol? type, Compilation compilation)
        {
            if (type is not INamedTypeSymbol { IsReferenceType: true } named)
            {
                return false;
            }

            var arity = named.Arity;
            if (arity is < 1 or > 8)
            {
                return false;
            }

            return SymbolEqualityComparer.Default.Equals(
                named.OriginalDefinition,
                compilation.GetTypeByMetadataName($"System.Tuple`{arity}"));
        }
    }
}
