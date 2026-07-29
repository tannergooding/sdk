// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading;
using System.Threading.Tasks;
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
