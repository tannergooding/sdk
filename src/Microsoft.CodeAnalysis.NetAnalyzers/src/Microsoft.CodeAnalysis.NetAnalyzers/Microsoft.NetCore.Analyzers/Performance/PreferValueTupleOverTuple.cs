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
    /// CA1880: <inheritdoc cref="PreferValueTupleOverTupleTitle"/>
    /// Flags usages of the reference-type <see cref="System.Tuple"/> family where the
    /// value-type <see cref="System.ValueTuple"/> family would avoid an allocation.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp, LanguageNames.VisualBasic)]
    public sealed class PreferValueTupleOverTupleAnalyzer : DiagnosticAnalyzer
    {
        internal const string RuleId = "CA1880";

        // By default only report on the non-public API surface, since converting an exposed 'Tuple'
        // is a source/binary breaking change. Users can opt in to the public surface via the
        // 'dotnet_code_quality.CA1880.api_surface' editorconfig option.
        private const SymbolVisibilityGroup DefaultSignatureVisibility = SymbolVisibilityGroup.Internal | SymbolVisibilityGroup.Private;

        internal static readonly DiagnosticDescriptor Rule = DiagnosticDescriptorHelper.Create(
            id: RuleId,
            title: CreateLocalizableResourceString(nameof(PreferValueTupleOverTupleTitle)),
            messageFormat: CreateLocalizableResourceString(nameof(PreferValueTupleOverTupleMessage)),
            category: DiagnosticCategory.Performance,
            ruleLevel: RuleLevel.IdeSuggestion,
            description: CreateLocalizableResourceString(nameof(PreferValueTupleOverTupleDescription)),
            isPortedFxCopRule: false,
            isDataflowRule: false);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

            context.RegisterCompilationStartAction(context =>
            {
                var tupleTypesBuilder = ImmutableArray.CreateBuilder<INamedTypeSymbol>(8);
                for (var arity = 1; arity <= 8; arity++)
                {
                    if (context.Compilation.TryGetOrCreateTypeByMetadataName($"System.Tuple`{arity}", out var tupleType))
                    {
                        tupleTypesBuilder.Add(tupleType);
                    }
                }

                if (tupleTypesBuilder.Count == 0)
                {
                    return;
                }

                var tupleTypes = tupleTypesBuilder.ToImmutable();

                // The non-generic 'System.Tuple' static class exposes the 'Tuple.Create' factory methods.
                _ = context.Compilation.TryGetOrCreateTypeByMetadataName(WellKnownTypeNames.SystemTuple, out var tupleFactoryType);

                // Member signatures: only reported for the configured (by default non-public) API surface.
                context.RegisterSymbolAction(context => AnalyzeSymbol(context, tupleTypes), SymbolKind.Field, SymbolKind.Property, SymbolKind.Method);

                // Member bodies: allocations are always an implementation detail, so they are always reported.
                context.RegisterOperationAction(context => AnalyzeObjectCreation(context, tupleTypes), OperationKind.ObjectCreation);

                if (tupleFactoryType is not null)
                {
                    context.RegisterOperationAction(context => AnalyzeInvocation(context, tupleFactoryType), OperationKind.Invocation);
                }
            });
        }

        private static void AnalyzeSymbol(SymbolAnalysisContext context, ImmutableArray<INamedTypeSymbol> tupleTypes)
        {
            var symbol = context.Symbol;

            // Compiler-generated members (e.g. property backing fields, accessor methods) are reported through
            // their associated user-authored symbol, so skip them to avoid duplicate diagnostics.
            if (symbol.IsImplicitlyDeclared)
            {
                return;
            }

            switch (symbol)
            {
                case IMethodSymbol method:
                    // Accessors are reported through their associated property/event, and an override cannot
                    // change the inherited signature, so neither is actionable here.
                    if (method.AssociatedSymbol is not null || method.IsOverride)
                    {
                        return;
                    }

                    // Constructors have no meaningful return type, but their parameters are still worth checking.
                    if (method.MethodKind is not (MethodKind.Constructor or MethodKind.StaticConstructor) &&
                        IsOrContainsTupleType(method.ReturnType, tupleTypes))
                    {
                        ReportSignature(context, method);
                        return;
                    }

                    foreach (var parameter in method.Parameters)
                    {
                        if (IsOrContainsTupleType(parameter.Type, tupleTypes))
                        {
                            ReportSignature(context, method);
                            return;
                        }
                    }

                    break;

                case IPropertySymbol property:
                    if (property.IsOverride)
                    {
                        return;
                    }

                    if (IsOrContainsTupleType(property.Type, tupleTypes))
                    {
                        ReportSignature(context, property);
                        return;
                    }

                    foreach (var parameter in property.Parameters)
                    {
                        if (IsOrContainsTupleType(parameter.Type, tupleTypes))
                        {
                            ReportSignature(context, property);
                            return;
                        }
                    }

                    break;

                case IFieldSymbol field:
                    if (IsOrContainsTupleType(field.Type, tupleTypes))
                    {
                        ReportSignature(context, field);
                    }

                    break;
            }
        }

        private static void ReportSignature(SymbolAnalysisContext context, ISymbol symbol)
        {
            if (!context.Options.MatchesConfiguredVisibility(Rule, symbol, context.Compilation, DefaultSignatureVisibility))
            {
                return;
            }

            context.ReportDiagnostic(symbol.CreateDiagnostic(Rule));
        }

        private static void AnalyzeObjectCreation(OperationAnalysisContext context, ImmutableArray<INamedTypeSymbol> tupleTypes)
        {
            var objectCreation = (IObjectCreationOperation)context.Operation;

            if (IsTupleType(objectCreation.Type, tupleTypes))
            {
                context.ReportDiagnostic(objectCreation.CreateDiagnostic(Rule));
            }
        }

        private static void AnalyzeInvocation(OperationAnalysisContext context, INamedTypeSymbol tupleFactoryType)
        {
            var invocation = (IInvocationOperation)context.Operation;
            var targetMethod = invocation.TargetMethod;

            if (targetMethod.IsStatic &&
                targetMethod.Name == "Create" &&
                SymbolEqualityComparer.Default.Equals(targetMethod.ContainingType, tupleFactoryType))
            {
                context.ReportDiagnostic(invocation.CreateDiagnostic(Rule));
            }
        }

        private static bool IsOrContainsTupleType(ITypeSymbol? type, ImmutableArray<INamedTypeSymbol> tupleTypes)
        {
            switch (type)
            {
                case IArrayTypeSymbol array:
                    return IsOrContainsTupleType(array.ElementType, tupleTypes);

                case INamedTypeSymbol named:
                    if (IsTupleType(named, tupleTypes))
                    {
                        return true;
                    }

                    foreach (var typeArgument in named.TypeArguments)
                    {
                        if (IsOrContainsTupleType(typeArgument, tupleTypes))
                        {
                            return true;
                        }
                    }

                    break;
            }

            return false;
        }

        private static bool IsTupleType(ITypeSymbol? type, ImmutableArray<INamedTypeSymbol> tupleTypes)
        {
            if (type is not INamedTypeSymbol named)
            {
                return false;
            }

            var originalDefinition = named.OriginalDefinition;

            foreach (var tupleType in tupleTypes)
            {
                if (SymbolEqualityComparer.Default.Equals(originalDefinition, tupleType))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
