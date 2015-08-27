using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nessos.LinqOptimizer;
using System.Linq.Expressions;
using Nessos.LinqOptimizer.Base;
using Nessos.LinqOptimizer.CSharp;
using Nessos.LinqOptimizer.Core;
using System.Reflection.Emit;
using System.Reflection;

namespace CompileSimple2
{
    public class Program
    {

        class Meow
        {
            public int Arg;
        }


        public int Gatto;
        public static void Main(string[] args)
        {
            new Program().MainInternal(args);
        }
        public void MainInternal(string[] args)
        {
            var zz = new Meow() { Arg = 4 };
            var arr = new[] { 46, 166, 468, 13, 156 };
            int captured = 6;
            Expression<Func<int[], int, IQueryExpr<int>>> template = (ar, cap) => ar.AsQueryExpr().Where(x => x > captured).Sum();
             template.Compile(true);
            var lambdaaa = Nessos.LinqOptimizer.Core.CoreHelpers.ToLambdaExpression(template);
            var compiled = CompileDebuggable(lambdaaa);
            //   var miao2 = ((Func<int[], int, int>)compiled)(arr, 400);
            var syntax = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(@"
using System.Linq;
using Nessos.LinqOptimizer.CSharp;

class Example
{
    static void Main()
    {
        System.Object.ReferenceEquals(null, null);
        var arr = new []{ 46, 166, 468, 13, 156 };
        var captured = 6;
        var p = arr.Where(x => x > 5555555).Any();
    }
}

");
            IEnumerable<MetadataReference> references = new[] {
                MetadataReference.CreateFromFile(typeof(string).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Nessos.LinqOptimizer.CSharp.Extensions).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Nessos.LinqOptimizer.Base.IQueryExpr).Assembly.Location),
            };
            var k = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("Miao", new[] { syntax }, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, concurrentBuild: false));
            var miao = syntax.GetRoot();
            foreach (var item in miao.DescendantNodesAndSelf().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>())
            {
                var linqTree = new LinqRewriter(k.GetSemanticModel(syntax, false));
                var z = linqTree.Visit(item);
                Console.WriteLine(z);
            }


            var rewriter = new LinqRewriter(k.GetSemanticModel(syntax, false));
            var changed = rewriter.Visit(miao);
            Console.WriteLine(changed.ToString());
            //var z = k.Emit(@"C:\temp\miao.dll");

        }


        private static List<LambdaExpression> lambdas = new List<LambdaExpression>();

        public static Delegate CompileDebuggable(LambdaExpression lambda)
        {
            //    return lambda.Compile();
            //   var modifiedLambda = (LambdaExpression)new ReplaceInMemoryObjectsVisitor(lambda).Visit(lambda);

            var asm = AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName("Emitted"), AssemblyBuilderAccess.Save, @"c:\temp");
            var mod = asm.DefineDynamicModule("Emitted", @"Emitted.dll");
            var type = mod.DefineType("EmittedCode", TypeAttributes.Public);
            lambdas.Add(lambda);
            var i = 0;
            foreach (var item in lambdas)
            {


                var met = type.DefineMethod("Lambda_" + i, MethodAttributes.Public | MethodAttributes.Static, item.ReturnType, item.Parameters.Select(x => x.Type).ToArray());
                try
                {
                    item.CompileToMethod(met);
                }
                catch (Exception)
                {
                }

                i++;
            }
            type.CreateType();
            asm.Save(@"Emitted.dll");

            return lambda.Compile();
        }
    }
}
