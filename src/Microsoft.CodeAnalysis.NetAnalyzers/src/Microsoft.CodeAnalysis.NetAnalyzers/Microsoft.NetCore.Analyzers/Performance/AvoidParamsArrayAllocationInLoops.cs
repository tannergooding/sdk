// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Analyzer.Utilities;
using Analyzer.Utilities.Extensions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Microsoft.NetCore.Analyzers.Performance
{
    using static MicrosoftNetCoreAnalyzersResources;

    /// <summary>
    /// CA1879: <inheritdoc cref="AvoidParamsArrayAllocationInLoopsTitle"/>
    /// Flags calls to <c>System.*</c> methods inside a loop where the compiler implicitly allocates
    /// a <c>params</c> array on every iteration. Allocations whose elements reference a local
    /// declared inside the loop (for example the iteration variable) vary per iteration and cannot
    /// be hoisted, so they are excluded.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp, LanguageNames.VisualBasic)]
    public sealed class AvoidParamsArrayAllocationInLoopsAnalyzer : DiagnosticAnalyzer
    {
        internal const string RuleId = "CA1879";

        internal static readonly DiagnosticDescriptor Rule = DiagnosticDescriptorHelper.Create(
            id: RuleId,
            title: CreateLocalizableResourceString(nameof(AvoidParamsArrayAllocationInLoopsTitle)),
            messageFormat: CreateLocalizableResourceString(nameof(AvoidParamsArrayAllocationInLoopsMessage)),
            category: DiagnosticCategory.Performance,
            ruleLevel: RuleLevel.IdeHidden_BulkConfigurable,
            description: CreateLocalizableResourceString(nameof(AvoidParamsArrayAllocationInLoopsDescription)),
            isPortedFxCopRule: false,
            isDataflowRule: false);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

            context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
        }

        private static void AnalyzeInvocation(OperationAnalysisContext context)
        {
            var invocation = (IInvocationOperation)context.Operation;

            // CS0231: a params parameter is always the last parameter, so only the final
            // argument can be an implicitly allocated params array.
            IArgumentOperation? paramsArgument = null;
            foreach (var argument in invocation.Arguments)
            {
                if (argument.Parameter?.IsParams == true)
                {
                    paramsArgument = argument;
                    break;
                }
            }

            if (paramsArgument is null ||
                paramsArgument.Value is not IArrayCreationOperation arrayCreation ||
                !IsImplicitParamsArrayAllocation(paramsArgument, arrayCreation) ||
                !IsSystemMethod(invocation.TargetMethod))
            {
                return;
            }

            var enclosingLoop = GetEnclosingLoop(invocation);
            if (enclosingLoop is null)
            {
                return;
            }

            // Only flag allocations that are invariant across the loop. If any element reads a local
            // declared inside the enclosing loop (most commonly the iteration variable), the array
            // cannot be hoisted out of the loop, so per dotnet/runtime#33793 cases 2 and 5 it must
            // not be flagged. This is an approximation: it does not account for fields mutated in the
            // loop or side-effecting calls, so those variant shapes are still (over-)reported. It
            // cheaply excludes the common per-iteration-variant shape without full flow analysis.
            if (ReferencesLoopLocal(arrayCreation, enclosingLoop))
            {
                return;
            }

            context.ReportDiagnostic(invocation.CreateDiagnostic(Rule, invocation.TargetMethod.Name));
        }

        /// <summary>
        /// Returns <see langword="true"/> when the argument for a <c>params</c> parameter is a
        /// non-empty array that the compiler implicitly allocated from expanded arguments. An empty
        /// params list resolves to <c>Array.Empty&lt;T&gt;()</c> and is not represented by an
        /// <see cref="IArrayCreationOperation"/>, so it is naturally excluded.
        /// </summary>
        private static bool IsImplicitParamsArrayAllocation(IArgumentOperation argument, IArrayCreationOperation arrayCreation)
        {
            // An explicitly passed array (e.g. an existing variable or an explicit 'new T[] { ... }')
            // is passed through as-is and is not an implicit allocation to flag here.
            if (argument.ArgumentKind != ArgumentKind.ParamArray)
            {
                return false;
            }

            return arrayCreation.Initializer is { ElementValues.IsEmpty: false };
        }

        private static bool IsSystemMethod(IMethodSymbol method)
        {
            var ns = method.ContainingType?.ContainingNamespace;

            while (ns is { IsGlobalNamespace: false })
            {
                if (ns.ContainingNamespace is { IsGlobalNamespace: true })
                {
                    return ns.Name == "System";
                }

                ns = ns.ContainingNamespace;
            }

            return false;
        }

        /// <summary>
        /// Returns the outermost enclosing <see cref="ILoopOperation"/> within the same execution
        /// frame, or <see langword="null"/> when the operation is not inside a loop. A lambda or
        /// local function establishes a new execution context whose invocation frequency relative to
        /// an outer loop is unknown, so the search stops at that boundary to avoid false positives.
        /// </summary>
        private static ILoopOperation? GetEnclosingLoop(IOperation operation)
        {
            ILoopOperation? enclosingLoop = null;

            for (var current = operation.Parent; current is not null; current = current.Parent)
            {
                switch (current.Kind)
                {
                    case OperationKind.Loop:
                        enclosingLoop = (ILoopOperation)current;
                        break;

                    case OperationKind.AnonymousFunction:
                    case OperationKind.LocalFunction:
                        return enclosingLoop;
                }
            }

            return enclosingLoop;
        }

        /// <summary>
        /// Returns <see langword="true"/> when any element of <paramref name="arrayCreation"/>
        /// references a local declared inside <paramref name="loop"/>, meaning the array varies per
        /// iteration and cannot be hoisted. Using the outermost enclosing loop naturally covers the
        /// iteration variables and body locals of any nested loops the allocation also sits in.
        /// Containment is decided by syntax span, which keeps this allocation-free and proportional
        /// to the number of elements rather than to the size of the loop body.
        /// </summary>
        private static bool ReferencesLoopLocal(IArrayCreationOperation arrayCreation, ILoopOperation loop)
        {
            var loopSyntax = loop.Syntax;

            foreach (var descendant in arrayCreation.Descendants())
            {
                if (descendant is not ILocalReferenceOperation localReference)
                {
                    continue;
                }

                foreach (var declaration in localReference.Local.DeclaringSyntaxReferences)
                {
                    if (declaration.SyntaxTree == loopSyntax.SyntaxTree &&
                        loopSyntax.Span.Contains(declaration.Span))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
