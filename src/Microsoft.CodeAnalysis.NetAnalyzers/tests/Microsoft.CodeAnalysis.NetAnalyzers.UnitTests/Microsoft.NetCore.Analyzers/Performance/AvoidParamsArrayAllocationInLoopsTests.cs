// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Testing;
using VerifyCS = Test.Utilities.CSharpCodeFixVerifier<
    Microsoft.NetCore.Analyzers.Performance.AvoidParamsArrayAllocationInLoopsAnalyzer,
    Microsoft.CodeAnalysis.Testing.EmptyCodeFixProvider>;
using VerifyVB = Test.Utilities.VisualBasicCodeFixVerifier<
    Microsoft.NetCore.Analyzers.Performance.AvoidParamsArrayAllocationInLoopsAnalyzer,
    Microsoft.CodeAnalysis.Testing.EmptyCodeFixProvider>;

namespace Microsoft.NetCore.Analyzers.Performance.UnitTests
{
    [TestClass]
    public class AvoidParamsArrayAllocationInLoopsTests
    {
        private const string CSharpSystemHelper = """


            namespace System
            {
                public static class Sys
                {
                    public static void Call(params object[] args) { }
                    public static void CallSingle(object arg) { }
                    public static void CallWithLeading(object first, params object[] args) { }
                    public static object CallReturning(params object[] args) => null;
                }
            }
            """;

        private const string VbSystemHelper = """


            Namespace System
                Public Module Sys
                    Public Sub Call2(ParamArray args As Object())
                    End Sub
                End Module
            End Namespace
            """;

        [TestMethod]
        public async Task WhileLoop_ImplicitParamsArray_DiagnosticAsync()
        {
            // lang=C#-test
            string source = """
                using System;

                public class C
                {
                    public void M()
                    {
                        int i = 0;
                        while (i++ < 10)
                        {
                            [|Sys.Call(1, 2, 3)|];
                        }
                    }
                }
                """ + CSharpSystemHelper;

            await VerifyCS.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task ForLoop_ImplicitParamsArray_DiagnosticAsync()
        {
            // lang=C#-test
            string source = """
                using System;

                public class C
                {
                    public void M()
                    {
                        for (int i = 0; i < 10; i++)
                        {
                            [|Sys.Call(1, 2, 3)|];
                        }
                    }
                }
                """ + CSharpSystemHelper;

            await VerifyCS.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task ForEachLoop_ImplicitParamsArray_DiagnosticAsync()
        {
            // lang=C#-test
            string source = """
                using System;

                public class C
                {
                    public void M(int[] items)
                    {
                        foreach (var item in items)
                        {
                            [|Sys.Call(1, 2, 3)|];
                        }
                    }
                }
                """ + CSharpSystemHelper;

            await VerifyCS.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task DoWhileLoop_ImplicitParamsArray_DiagnosticAsync()
        {
            // lang=C#-test
            string source = """
                using System;

                public class C
                {
                    public void M()
                    {
                        int i = 0;
                        do
                        {
                            [|Sys.Call(1, 2, 3)|];
                        }
                        while (i++ < 10);
                    }
                }
                """ + CSharpSystemHelper;

            await VerifyCS.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task NestedLoop_ImplicitParamsArray_DiagnosticAsync()
        {
            // lang=C#-test
            string source = """
                using System;

                public class C
                {
                    public void M()
                    {
                        for (int i = 0; i < 10; i++)
                        {
                            for (int j = 0; j < 10; j++)
                            {
                                [|Sys.Call(1, 2, 3)|];
                            }
                        }
                    }
                }
                """ + CSharpSystemHelper;

            await VerifyCS.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task Loop_ElementUsesIterationVariable_NoDiagnosticAsync()
        {
            // dotnet/runtime#33793 cases 2 and 5: an element that varies per iteration (here the
            // loop variable 'i') means the allocation cannot be hoisted out of the loop, so it must
            // not be flagged. See ReferencesLoopLocal in the analyzer.
            // lang=C#-test
            string source = """
                using System;

                public class C
                {
                    public void M()
                    {
                        for (int i = 0; i < 10; i++)
                        {
                            Sys.Call(i, 2, 3);
                        }
                    }
                }
                """ + CSharpSystemHelper;

            await VerifyCS.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task Loop_ElementUsesLocalDeclaredInLoopBody_NoDiagnosticAsync()
        {
            // lang=C#-test
            string source = """
                using System;

                public class C
                {
                    public void M()
                    {
                        while (true)
                        {
                            int local = 42;
                            Sys.Call(local, 2, 3);
                        }
                    }
                }
                """ + CSharpSystemHelper;

            await VerifyCS.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task ForEachLoop_ElementUsesIterationVariable_NoDiagnosticAsync()
        {
            // lang=C#-test
            string source = """
                using System;

                public class C
                {
                    public void M(int[] items)
                    {
                        foreach (var item in items)
                        {
                            Sys.Call(item, 2, 3);
                        }
                    }
                }
                """ + CSharpSystemHelper;

            await VerifyCS.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task NestedLoop_ElementUsesOuterIterationVariable_NoDiagnosticAsync()
        {
            // lang=C#-test
            string source = """
                using System;

                public class C
                {
                    public void M()
                    {
                        for (int i = 0; i < 10; i++)
                        {
                            for (int j = 0; j < 10; j++)
                            {
                                Sys.Call(i, 2, 3);
                            }
                        }
                    }
                }
                """ + CSharpSystemHelper;

            await VerifyCS.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task Loop_ElementUsesLocalDeclaredOutsideLoop_DiagnosticAsync()
        {
            // A local declared outside the loop is invariant across iterations, so the allocation is
            // hoistable and is still flagged. This guards against the exclusion over-reaching.
            // lang=C#-test
            string source = """
                using System;

                public class C
                {
                    public void M()
                    {
                        int outer = 42;
                        for (int i = 0; i < 10; i++)
                        {
                            [|Sys.Call(outer, 2, 3)|];
                        }
                    }
                }
                """ + CSharpSystemHelper;

            await VerifyCS.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task LeadingParameterBeforeParams_DiagnosticAsync()
        {
            // The params array is not the first argument. The analyzer locates it by Parameter.IsParams
            // rather than by position, so a preceding parameter must not shift or hide the detection.
            // lang=C#-test
            string source = """
                using System;

                public class C
                {
                    public void M()
                    {
                        for (int i = 0; i < 10; i++)
                        {
                            [|Sys.CallWithLeading("first", 1, 2, 3)|];
                        }
                    }
                }
                """ + CSharpSystemHelper;

            await VerifyCS.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task LeadingArgumentVariantParamsInvariant_DiagnosticAsync()
        {
            // Only the leading (non-params) argument varies per iteration; the params elements are
            // invariant, so the allocation is still hoistable and must be flagged. This confirms the
            // invariance check inspects the params allocation, not the whole argument list.
            // lang=C#-test
            string source = """
                using System;

                public class C
                {
                    public void M()
                    {
                        for (int i = 0; i < 10; i++)
                        {
                            [|Sys.CallWithLeading(i, 1, 2, 3)|];
                        }
                    }
                }
                """ + CSharpSystemHelper;

            await VerifyCS.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task NamedLeadingArgument_DiagnosticAsync()
        {
            // The leading parameter is passed by name with the params still implicitly expanded from
            // trailing positional arguments. Naming a non-params argument must not disturb locating
            // the params allocation, which is matched on Parameter.IsParams rather than by position.
            // lang=C#-test
            string source = """
                using System;

                public class C
                {
                    public void M()
                    {
                        for (int i = 0; i < 10; i++)
                        {
                            [|Sys.CallWithLeading(first: "first", 1, 2, 3)|];
                        }
                    }
                }
                """ + CSharpSystemHelper;

            await VerifyCS.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task NamedParamsExplicitArray_NoDiagnosticAsync()
        {
            // Naming the params parameter requires passing an explicit array, which is a pass-through
            // (ArgumentKind.Explicit), not an implicit per-iteration allocation, so it is not flagged.
            // lang=C#-test
            string source = """
                using System;

                public class C
                {
                    public void M()
                    {
                        for (int i = 0; i < 10; i++)
                        {
                            Sys.CallWithLeading(first: "first", args: new object[] { 1, 2, 3 });
                        }
                    }
                }
                """ + CSharpSystemHelper;

            await VerifyCS.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task NestedInvocations_BothDiagnosticAsync()
        {
            // Nested occurrences: the outer call's params array contains another params invocation.
            // Both allocate per iteration, so both are reported and neither hides the other.
            // lang=C#-test
            string source = """
                using System;

                public class C
                {
                    public void M()
                    {
                        for (int i = 0; i < 10; i++)
                        {
                            [|Sys.Call(1, [|Sys.CallReturning(2, 3)|], 4)|];
                        }
                    }
                }
                """ + CSharpSystemHelper;

            await VerifyCS.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task MatchNestedInsideEnclosingExpression_DiagnosticAsync()
        {
            // The match sits inside another expression, so the reported node must be the invocation
            // itself rather than the enclosing argument or assignment.
            // lang=C#-test
            string source = """
                using System;

                public class C
                {
                    public void M()
                    {
                        for (int i = 0; i < 10; i++)
                        {
                            Console.WriteLine([|Sys.CallReturning(1, 2, 3)|]);
                        }
                    }
                }
                """ + CSharpSystemHelper;

            await VerifyCS.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task DiagnosticMessage_IncludesMethodNameAsync()
        {
            // lang=C#-test
            string source = """
                using System;

                public class C
                {
                    public void M()
                    {
                        while (true)
                        {
                            {|#0:Sys.Call(1, 2, 3)|};
                        }
                    }
                }
                """ + CSharpSystemHelper;

            await VerifyCS.VerifyAnalyzerAsync(
                source,
                VerifyCS.Diagnostic(AvoidParamsArrayAllocationInLoopsAnalyzer.Rule)
                    .WithLocation(0)
                    .WithArguments("Call"));
        }

        [TestMethod]
        public async Task RealWorld_StringFormatInLoop_DiagnosticAsync()
        {
            // lang=C#-test
            string source = """
                using System;

                public class C
                {
                    public void M()
                    {
                        for (int i = 0; i < 10; i++)
                        {
                            var s = [|string.Format("{0}{1}{2}{3}", 1, 2, 3, 4)|];
                        }
                    }
                }
                """;

            await VerifyCS.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task NotInLoop_NoDiagnosticAsync()
        {
            // lang=C#-test
            string source = """
                using System;

                public class C
                {
                    public void M()
                    {
                        Sys.Call(1, 2, 3);
                    }
                }
                """ + CSharpSystemHelper;

            await VerifyCS.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task ExplicitArrayArgument_NoDiagnosticAsync()
        {
            // lang=C#-test
            string source = """
                using System;

                public class C
                {
                    public void M(object[] args)
                    {
                        while (true)
                        {
                            Sys.Call(args);
                        }
                    }
                }
                """ + CSharpSystemHelper;

            await VerifyCS.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task ExplicitNewArrayArgument_NoDiagnosticAsync()
        {
            // lang=C#-test
            string source = """
                using System;

                public class C
                {
                    public void M()
                    {
                        while (true)
                        {
                            Sys.Call(new object[] { 1, 2, 3 });
                        }
                    }
                }
                """ + CSharpSystemHelper;

            await VerifyCS.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task EmptyParamsArgument_NoDiagnosticAsync()
        {
            // lang=C#-test
            string source = """
                using System;

                public class C
                {
                    public void M()
                    {
                        while (true)
                        {
                            Sys.Call();
                        }
                    }
                }
                """ + CSharpSystemHelper;

            await VerifyCS.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task NonParamsOverload_NoDiagnosticAsync()
        {
            // lang=C#-test
            string source = """
                using System;

                public class C
                {
                    public void M()
                    {
                        while (true)
                        {
                            Sys.CallSingle(1);
                        }
                    }
                }
                """ + CSharpSystemHelper;

            await VerifyCS.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task NonSystemMethod_NoDiagnosticAsync()
        {
            // lang=C#-test
            string source = """
                public class C
                {
                    public void M()
                    {
                        while (true)
                        {
                            MyOwnMethod(1, 2, 3);
                        }
                    }

                    private static void MyOwnMethod(params object[] args) { }
                }
                """;

            await VerifyCS.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task NestedSystemNamespace_DiagnosticAsync()
        {
            // The method's outermost namespace is 'System' (System.Text.*), so it is treated as BCL.
            // lang=C#-test
            string source = """
                using System.Text;

                public class C
                {
                    public void M()
                    {
                        while (true)
                        {
                            [|Helper.Call(1, 2, 3)|];
                        }
                    }
                }

                namespace System.Text
                {
                    public static class Helper
                    {
                        public static void Call(params object[] args) { }
                    }
                }
                """;

            await VerifyCS.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task NamespaceEndingInSystem_NoDiagnosticAsync()
        {
            // The outermost namespace is 'MyApp', not 'System', so it is not treated as BCL.
            // lang=C#-test
            string source = """
                using MyApp.System;

                public class C
                {
                    public void M()
                    {
                        while (true)
                        {
                            Helper.Call(1, 2, 3);
                        }
                    }
                }

                namespace MyApp.System
                {
                    public static class Helper
                    {
                        public static void Call(params object[] args) { }
                    }
                }
                """;

            await VerifyCS.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task InsideLambdaInLoop_NoDiagnosticAsync()
        {
            // lang=C#-test
            string source = """
                using System;

                public class C
                {
                    public void M()
                    {
                        for (int i = 0; i < 10; i++)
                        {
                            Action a = () => Sys.Call(1, 2, 3);
                        }
                    }
                }
                """ + CSharpSystemHelper;

            await VerifyCS.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task InsideLocalFunctionInLoop_NoDiagnosticAsync()
        {
            // lang=C#-test
            string source = """
                using System;

                public class C
                {
                    public void M()
                    {
                        for (int i = 0; i < 10; i++)
                        {
                            void Local() => Sys.Call(1, 2, 3);
                        }
                    }
                }
                """ + CSharpSystemHelper;

            await VerifyCS.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task Basic_ForLoop_ImplicitParamsArray_DiagnosticAsync()
        {
            // lang=VB-test
            string source = """
                Imports System

                Public Class C
                    Public Sub M()
                        For i As Integer = 0 To 9
                            [|Sys.Call2(1, 2, 3)|]
                        Next
                    End Sub
                End Class
                """ + VbSystemHelper;

            await VerifyVB.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task Basic_ElementUsesIterationVariable_NoDiagnosticAsync()
        {
            // lang=VB-test
            string source = """
                Imports System

                Public Class C
                    Public Sub M()
                        For i As Integer = 0 To 9
                            Sys.Call2(i, 2, 3)
                        Next
                    End Sub
                End Class
                """ + VbSystemHelper;

            await VerifyVB.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task Basic_NotInLoop_NoDiagnosticAsync()
        {
            // lang=VB-test
            string source = """
                Imports System

                Public Class C
                    Public Sub M()
                        Sys.Call2(1, 2, 3)
                    End Sub
                End Class
                """ + VbSystemHelper;

            await VerifyVB.VerifyAnalyzerAsync(source);
        }

        [TestMethod]
        public async Task Basic_NonSystemMethod_NoDiagnosticAsync()
        {
            // lang=VB-test
            string source = """
                Public Class C
                    Public Sub M()
                        While True
                            MyOwnMethod(1, 2, 3)
                        End While
                    End Sub

                    Private Shared Sub MyOwnMethod(ParamArray args As Object())
                    End Sub
                End Class
                """;

            await VerifyVB.VerifyAnalyzerAsync(source);
        }
    }
}
