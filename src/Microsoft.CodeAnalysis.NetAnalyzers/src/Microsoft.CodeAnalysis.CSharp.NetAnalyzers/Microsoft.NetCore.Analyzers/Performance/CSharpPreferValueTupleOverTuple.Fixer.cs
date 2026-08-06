// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Analyzer.Utilities;
using Analyzer.Utilities.Extensions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.NetAnalyzers;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.NetCore.Analyzers;
using Microsoft.NetCore.Analyzers.Performance;

namespace Microsoft.NetCore.CSharp.Analyzers.Performance
{
    /// <summary>
    /// CA1878: Prefer 'ValueTuple' over 'Tuple'.
    /// Rewrites 'new Tuple&lt;...&gt;(...)' and 'Tuple.Create(...)' callsites to their 'ValueTuple'
    /// equivalents. The generic type arguments are preserved, so the rewrite does not change the
    /// element types (unlike converting to C# tuple syntax, which would re-run type inference).
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp), Shared]
    public sealed class CSharpPreferValueTupleOverTupleFixer : SyntaxEditorBasedCodeFixProvider
    {
        private const string ValueTupleName = "ValueTuple";

        public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create(PreferValueTupleOverTupleAnalyzer.RuleId);

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

            // Swapping only the callsite can produce code that no longer means what it did. It breaks the
            // build when the allocation flows into a location independently typed as the reference 'Tuple'
            // (e.g. 'Tuple<int, string> t = Tuple.Create(...)'), and it silently changes '==' from reference
            // to structural equality when the value is compared. In either case still report the diagnostic,
            // but do not offer a fix.
            var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            if (semanticModel is null || FixWouldChangeMeaning(semanticModel, node, context.CancellationToken))
            {
                return;
            }

            RegisterCodeFix(
                context,
                MicrosoftNetCoreAnalyzersResources.PreferValueTupleOverTupleCodeFixTitle,
                nameof(MicrosoftNetCoreAnalyzersResources.PreferValueTupleOverTupleCodeFixTitle));
        }

        protected override async Task ApplyFixAsync(Document document, Diagnostic diagnostic, SyntaxEditor editor, CancellationToken cancellationToken)
        {
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var node = editor.OriginalRoot.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);

            // A fix-all pass is handed every diagnostic the rule reported, including the signature and
            // meaning-changing shapes registration declines, so the eligibility check runs again here.
            if (GetTupleNameToReplace(node) is not { } tupleName ||
                semanticModel is null ||
                FixWouldChangeMeaning(semanticModel, node, cancellationToken))
            {
                return;
            }

            // The lambda overload, so that an enclosing rewrite observes the nested ones already applied.
            editor.ReplaceNode(tupleName, (currentNode, _) => WithValueTupleIdentifier((SimpleNameSyntax)currentNode));
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

        private static bool FixWouldChangeMeaning(SemanticModel semanticModel, SyntaxNode allocation, CancellationToken cancellationToken)
            => ConversionWouldBreakBuild(semanticModel, allocation, cancellationToken)
                || EqualityComparisonWouldChangeSemantics(semanticModel, allocation, cancellationToken);

        // 'Tuple' derives '==' from 'object' (reference identity), whereas 'ValueTuple' overloads it to compare
        // element-wise, and the swap compiles either way. So a reported allocation whose value is compared with
        // '==' or '!=' - directly, or through the local it initializes - would change results with no diagnostic
        // to warn the user. Decline the fix rather than change behavior silently.
        private static bool EqualityComparisonWouldChangeSemantics(SemanticModel semanticModel, SyntaxNode allocation, CancellationToken cancellationToken)
        {
            var expression = (ExpressionSyntax)allocation;

            if (IsEqualityOperand(semanticModel, expression, cancellationToken))
            {
                return true;
            }

            while (expression.Parent is ParenthesizedExpressionSyntax parenthesized)
            {
                expression = parenthesized;
            }

            // Only a 'var' local reaches here as fixable; an explicit 'Tuple' local is already declined by the
            // conversion check. Walk the operation tree from the declaration up to its enclosing executable body,
            // which finds every reference for a method body, top-level statements, a lambda, or an initializer
            // alike - a syntactic block walk would miss them all for a top-level local, which has no enclosing
            // block. Symbol identity keeps look-alikes in sibling scopes out.
            if (expression.Parent is not EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator } ||
                semanticModel.GetDeclaredSymbol(declarator, cancellationToken) is not ILocalSymbol local ||
                semanticModel.GetOperation(declarator, cancellationToken) is not { } declaration)
            {
                return false;
            }

            foreach (var reference in declaration.GetRoot().Descendants().OfType<ILocalReferenceOperation>())
            {
                if (SymbolEqualityComparer.Default.Equals(reference.Local, local) &&
                    reference.Syntax is ExpressionSyntax referenceSyntax &&
                    IsEqualityOperand(semanticModel, referenceSyntax, cancellationToken))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsEqualityOperand(SemanticModel semanticModel, ExpressionSyntax expression, CancellationToken cancellationToken)
        {
            while (expression.Parent is ParenthesizedExpressionSyntax parenthesized)
            {
                expression = parenthesized;
            }

            return expression.Parent is BinaryExpressionSyntax binary &&
                (binary.Left == expression || binary.Right == expression) &&
                semanticModel.GetOperation(binary, cancellationToken) is IBinaryOperation { OperatorKind: BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals };
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

            return compilation.TryGetOrCreateTypeByMetadataName(PreferValueTupleOverTupleAnalyzer.TupleTypeNames[arity - 1], out var tupleType) &&
                SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, tupleType);
        }
    }
}
