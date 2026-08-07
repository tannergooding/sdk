// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Testing;
using VerifyCS = Test.Utilities.CSharpCodeFixVerifier<
    Microsoft.NetCore.Analyzers.Performance.PreferValueTupleOverTupleAnalyzer,
    Microsoft.NetCore.CSharp.Analyzers.Performance.CSharpPreferValueTupleOverTupleFixer>;
using VerifyVB = Test.Utilities.VisualBasicCodeFixVerifier<
    Microsoft.NetCore.Analyzers.Performance.PreferValueTupleOverTupleAnalyzer,
    Microsoft.CodeAnalysis.Testing.EmptyCodeFixProvider>;

namespace Microsoft.NetCore.Analyzers.Performance.UnitTests
{
    [TestClass]
    public class PreferValueTupleOverTupleTests
    {
        [TestMethod]
        public async Task ObjectCreation_Diagnostic_AndFixAsync()
        {
            var source = """
                using System;

                public class C
                {
                    public void M()
                    {
                        var t = [|new Tuple<int, string>(1, "a")|];
                    }
                }
                """;
            var fixedSource = """
                using System;

                public class C
                {
                    public void M()
                    {
                        var t = new ValueTuple<int, string>(1, "a");
                    }
                }
                """;
            await VerifyCS.VerifyCodeFixAsync(source, fixedSource);
        }

        [TestMethod]
        public async Task TupleCreate_Diagnostic_AndFixAsync()
        {
            var source = """
                using System;

                public class C
                {
                    public void M()
                    {
                        var t = [|Tuple.Create(1, "a")|];
                    }
                }
                """;
            var fixedSource = """
                using System;

                public class C
                {
                    public void M()
                    {
                        var t = ValueTuple.Create(1, "a");
                    }
                }
                """;
            await VerifyCS.VerifyCodeFixAsync(source, fixedSource);
        }

        [TestMethod]
        public async Task TupleCreate_NamedAndReorderedArguments_Diagnostic_AndFixAsync()
        {
            // The fix swaps only the 'Tuple' name and leaves the argument list untouched, so named and
            // reordered arguments are preserved verbatim and the rewrite stays correct.
            var source = """
                using System;

                public class C
                {
                    public void M()
                    {
                        var t = [|Tuple.Create(item2: "a", item1: 1)|];
                    }
                }
                """;
            var fixedSource = """
                using System;

                public class C
                {
                    public void M()
                    {
                        var t = ValueTuple.Create(item2: "a", item1: 1);
                    }
                }
                """;
            await VerifyCS.VerifyCodeFixAsync(source, fixedSource);
        }

        [TestMethod]
        public async Task FullyQualifiedTupleCreate_Diagnostic_AndFixAsync()
        {
            var source = """
                public class C
                {
                    public void M()
                    {
                        var t = [|System.Tuple.Create(1, "a")|];
                    }
                }
                """;
            var fixedSource = """
                public class C
                {
                    public void M()
                    {
                        var t = System.ValueTuple.Create(1, "a");
                    }
                }
                """;
            await VerifyCS.VerifyCodeFixAsync(source, fixedSource);
        }

        [TestMethod]
        public async Task FullyQualifiedObjectCreation_Diagnostic_AndFixAsync()
        {
            var source = """
                public class C
                {
                    public void M()
                    {
                        var t = [|new System.Tuple<int, string>(1, "a")|];
                    }
                }
                """;
            var fixedSource = """
                public class C
                {
                    public void M()
                    {
                        var t = new System.ValueTuple<int, string>(1, "a");
                    }
                }
                """;
            await VerifyCS.VerifyCodeFixAsync(source, fixedSource);
        }

        [TestMethod]
        public async Task NestedTupleCreate_Diagnostic_AndFixAllAsync()
        {
            // Nested reports exercise fix-all: each fix rewrites only its own callsite's 'Tuple'
            // name, so the edits are disjoint and converge in a single iteration.
            var source = """
                using System;

                public class C
                {
                    public void M()
                    {
                        var t = {|CA1880:Tuple.Create(1, {|CA1880:Tuple.Create(2, 3)|})|};
                    }
                }
                """;
            var fixedSource = """
                using System;

                public class C
                {
                    public void M()
                    {
                        var t = ValueTuple.Create(1, ValueTuple.Create(2, 3));
                    }
                }
                """;
            await VerifyCS.VerifyCodeFixAsync(source, fixedSource);
        }

        [TestMethod]
        public async Task TriviaAroundFixedNode_PreservedAsync()
        {
            var source = """
                using System;

                public class C
                {
                    public void M()
                    {
                        // leading comment
                        var t = [|Tuple.Create(1, "a")|]; // trailing comment
                    }
                }
                """;
            var fixedSource = """
                using System;

                public class C
                {
                    public void M()
                    {
                        // leading comment
                        var t = ValueTuple.Create(1, "a"); // trailing comment
                    }
                }
                """;
            await VerifyCS.VerifyCodeFixAsync(source, fixedSource);
        }

        [TestMethod]
        public async Task VisualBasic_Allocation_DiagnosticAsync()
        {
            var source = """
                Imports System

                Public Class C
                    Public Sub M()
                        Dim t = [|Tuple.Create(1, "a")|]
                        Dim u = [|New Tuple(Of Integer, String)(1, "a")|]
                    End Sub
                End Class
                """;
            await VerifyVB.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task VisualBasic_ValueTuple_NoDiagnosticAsync()
        {
            var source = """
                Imports System

                Public Class C
                    Public Sub M()
                        Dim t = ValueTuple.Create(1, "a")
                        Dim u = New ValueTuple(Of Integer, String)(1, "a")
                    End Sub
                End Class
                """;
            await VerifyVB.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task Signature_NoFixOfferedAsync()
        {
            // Signature diagnostics share the rule ID but are not fixable; the fixer must not offer a
            // no-op lightbulb for them. An unchanged expected document asserts no fix is registered.
            var source = """
                using System;

                internal class C
                {
                    private Tuple<int, string> [|_field|];

                    private Tuple<int, string> [|M|]() => null;
                }
                """;
            await VerifyCS.VerifyCodeFixAsync(source, source);
        }

        [TestMethod]
        public async Task MixedFixableAndUnfixable_FixAllFixesOnlyTheFixableAsync()
        {
            // Fix-all is handed every diagnostic the rule reported, not just the ones registration
            // accepted, so the eligibility check has to run again per diagnostic. Here one document
            // carries all three shapes: a fixable allocation, a signature, and an allocation flowing
            // into an explicitly typed 'Tuple' local. Only the first may be rewritten.
            var source = """
                using System;

                internal class C
                {
                    private Tuple<int, string> [|_field|];

                    public void M()
                    {
                        var fixable = [|Tuple.Create(1, "a")|];
                        Tuple<int, string> pinned = [|Tuple.Create(2, "b")|];
                    }
                }
                """;
            var fixedSource = """
                using System;

                internal class C
                {
                    private Tuple<int, string> _field;

                    public void M()
                    {
                        var fixable = ValueTuple.Create(1, "a");
                        Tuple<int, string> pinned = Tuple.Create(2, "b");
                    }
                }
                """;
            await new VerifyCS.Test
            {
                TestCode = source,
                FixedCode = fixedSource,
                // The two declined shapes still report after the fix-all pass; only the allocation
                // assigned to 'var' is rewritten.
                FixedState =
                {
                    ExpectedDiagnostics =
                    {
                        VerifyCS.Diagnostic().WithSpan(5, 32, 5, 38),
                        VerifyCS.Diagnostic().WithSpan(10, 37, 10, 57),
                    },
                },
            }.RunAsync(CancellationToken.None);
        }

        [TestMethod]
        public async Task ExplicitTupleTarget_NoFixOfferedAsync()
        {
            // The allocation is still worth flagging, but swapping only the callsite would leave a
            // 'ValueTuple' assigned to an explicitly typed 'Tuple' local, which does not compile. The
            // fixer must report and offer no fix rather than produce a build break.
            var source = """
                using System;

                internal class C
                {
                    private void M()
                    {
                        Tuple<int, string> t = [|Tuple.Create(1, "a")|];
                    }
                }
                """;
            await VerifyCS.VerifyCodeFixAsync(source, source);
        }

        [TestMethod]
        public async Task NestedExplicitTupleTarget_NoFixOfferedAsync()
        {
            // Both allocations are unfixable: the outer flows into an explicit 'Tuple' local, and the
            // inner is an argument to the outer 'Tuple' constructor whose parameter type is fixed. Neither
            // can be rewritten in isolation without breaking the build, so no fix is offered for either.
            var source = """
                using System;

                internal class C
                {
                    private void M()
                    {
                        Tuple<int, Tuple<string, int>> t = {|CA1880:new Tuple<int, Tuple<string, int>>(1, {|CA1880:new Tuple<string, int>("a", 2)|})|};
                    }
                }
                """;
            await VerifyCS.VerifyCodeFixAsync(source, source);
        }

        [TestMethod]
        public async Task DirectEqualityComparison_NoFixOfferedAsync()
        {
            // 'Tuple' compares by reference, 'ValueTuple' element-wise, and both compile. Rewriting the
            // operands would silently flip the result, so the diagnostic stands but no fix is offered.
            var source = """
                using System;

                internal class C
                {
                    private bool M()
                    {
                        return {|CA1880:Tuple.Create(1, "a")|} == {|CA1880:Tuple.Create(1, "a")|};
                    }
                }
                """;
            await VerifyCS.VerifyCodeFixAsync(source, source);
        }

        [TestMethod]
        public async Task EqualityComparisonThroughLocal_NoFixOfferedAsync()
        {
            // The comparison is on the 'var' locals the allocations initialize rather than on the
            // allocations directly, but rewriting them changes '==' from reference to structural equality
            // just the same, so neither allocation is offered a fix.
            var source = """
                using System;

                internal class C
                {
                    private bool M()
                    {
                        var t = {|CA1880:Tuple.Create(1, "a")|};
                        var other = {|CA1880:Tuple.Create(1, "a")|};
                        return t != other;
                    }
                }
                """;
            await VerifyCS.VerifyCodeFixAsync(source, source);
        }

        [TestMethod]
        public async Task EqualityComparisonThroughLocal_TopLevelStatements_NoFixOfferedAsync()
        {
            // A local declared in top-level statements has no enclosing block, so its references - and the
            // '==' among them - are found through the operation tree rather than a syntactic block walk. The
            // value is compared, so the fix is declined here just as it is inside a method body.
            var source = """
                using System;

                var t = {|CA1880:Tuple.Create(1, "a")|};
                var u = t;
                Console.WriteLine(t == u);
                """;
            await new VerifyCS.Test
            {
                ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
                LanguageVersion = LanguageVersion.CSharp9,
                TestState =
                {
                    Sources = { source },
                    OutputKind = OutputKind.ConsoleApplication,
                },
                FixedState =
                {
                    Sources = { source },
                    OutputKind = OutputKind.ConsoleApplication,
                },
            }.RunAsync(CancellationToken.None);
        }

        [TestMethod]
        public async Task ValueTuple_NoDiagnosticAsync()
        {
            var source = """
                using System;

                public class C
                {
                    private ValueTuple<int, string> _field;

                    public void M()
                    {
                        var t = ValueTuple.Create(1, "a");
                        var u = new ValueTuple<int, string>(1, "a");
                        var v = (1, "a");
                    }
                }
                """;
            await VerifyCS.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task NonPublicSignatures_DiagnosticAsync()
        {
            var source = """
                using System;
                using System.Collections.Generic;

                internal class C
                {
                    private Tuple<int, string> [|_field|];
                    private Tuple<int, string> [|P|] { get; set; }
                    private Tuple<int, string>[] [|_array|];
                    private List<Tuple<int, string>> [|_list|];

                    private Tuple<int, string> [|M|]() => null;
                    private void [|N|](Tuple<int, string> arg) { }
                }
                """;
            await VerifyCS.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task PublicSignatures_NoDiagnosticByDefaultAsync()
        {
            var source = """
                using System;

                public class C
                {
                    public Tuple<int, string> Field;
                    public Tuple<int, string> P { get; set; }

                    public Tuple<int, string> M() => null;
                    public void N(Tuple<int, string> arg) { }
                }
                """;
            await VerifyCS.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task PublicSignatures_DiagnosticWhenApiSurfaceAllAsync()
        {
            var source = """
                using System;

                public class C
                {
                    public Tuple<int, string> [|Field|];
                    public Tuple<int, string> [|P|] { get; set; }

                    public Tuple<int, string> [|M|]() => null;
                    public void [|N|](Tuple<int, string> arg) { }
                }
                """;

            await new VerifyCS.Test()
            {
                TestState =
                {
                    Sources = { source },
                    AnalyzerConfigFiles = { ("/.editorconfig", "[*]\r\ndotnet_code_quality.CA1880.api_surface = all") },
                },
            }.RunAsync(CancellationToken.None);
        }

        [TestMethod]
        public async Task OverrideSignature_NoDiagnosticAsync()
        {
            var source = """
                using System;

                internal abstract class Base
                {
                    protected abstract Tuple<int, string> [|M|]();
                }

                internal sealed class Derived : Base
                {
                    protected override Tuple<int, string> M() => null;
                }
                """;
            await VerifyCS.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task Allocation_ReportedEvenInPublicMethodAsync()
        {
            // Allocations are an implementation detail, so they are reported regardless of the
            // containing member's accessibility.
            var source = """
                using System;

                public class C
                {
                    public void M()
                    {
                        var t = [|Tuple.Create(1, "a")|];
                    }
                }
                """;
            await VerifyCS.VerifyAnalyzerAsync(source);
        }
    }
}
