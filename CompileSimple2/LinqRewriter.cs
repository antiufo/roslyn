using Microsoft.CodeAnalysis.CSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Linq.Expressions;
using System.Reflection;
using Nessos.LinqOptimizer.Base;

namespace CompileSimple2
{
    class LinqRewriter : CSharpSyntaxRewriter
    {
        private SemanticModel semantic;
        public LinqRewriter(SemanticModel semantic)
        {
            this.semantic = semantic;
        }


        public override SyntaxNode DefaultVisit(SyntaxNode node)
        {
            return base.DefaultVisit(node);
        }

        public override SyntaxNode VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var methodExpr = node.Expression as MemberAccessExpressionSyntax;
            if (methodExpr != null)
            {
                var objSymbol = semantic.GetSymbolInfo(methodExpr.Expression).Symbol;
                var obj = objSymbol is ITypeSymbol ? null : Visit(methodExpr.Expression);
                var declaringType = objSymbol is ITypeSymbol ? semantic.GetType((ITypeSymbol)objSymbol) : null;
                var methodSymbol = (IMethodSymbol)semantic.GetSymbolInfo(methodExpr).Symbol;
                var method = semantic.GetMethod(methodSymbol);
                var alternate = LinqOptimizerMethods.SingleOrDefault(x =>
                {
                    if (x.Name != method.Name) return false;
                    var p1 = x.GetParameters();
                    var p2 = method.GetParameters();
                    if (p1.Length != p2.Length) return false;
                    for (int i = 0; i < p1.Length; i++)
                    {
                        var a1 = p1[i].ParameterType;
                        if (a1.IsGenericType && a1.GetGenericTypeDefinition() == typeof(Expression<>))
                            a1 = a1.GetGenericArguments()[0];

                        if (a1.IsGenericType && a1.GetGenericTypeDefinition() == typeof(IQueryExpr<>))
                            a1 = a1.GetGenericArguments()[0];

                        var a2 = p2[i].ParameterType;
                        if (a1 == a2) continue;
                        if (a1.IsGenericType != a2.IsGenericType) return false;
                        var gen1 = a1.GetGenericArguments();
                        var gen2 = a2.GetGenericArguments();
                        if (gen1.Length != gen2.Length) return false;
                    }

                    return true;
                });
                if (alternate == null) return node;

                var n = MakeMemberAccess(MakeMemberAccess(MakeMemberAccess(MakeMemberAccess(SyntaxFactory.IdentifierName("Nessos"), "LinqOptimizer"), "CSharp"), "Extensions"), "AsQueryExpr");
                var asqexpr = SyntaxFactory.InvocationExpression(n, SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList<ArgumentSyntax>(new[] { SyntaxFactory.Argument((ExpressionSyntax)obj) })));

                var id = (++lastId).ToString();
                var annotation = new SyntaxAnnotation("LinqRewrite", id);
                var changed = node.WithExpression(MakeMemberAccess(asqexpr, method.Name)).WithAdditionalAnnotations(annotation);
                var updated = node.SyntaxTree.GetRoot().ReplaceNode(node, changed).SyntaxTree;
                var k = updated.GetRoot().GetAnnotatedNodes(annotation).First();
                var comp = semantic.Compilation.ReplaceSyntaxTree(node.SyntaxTree, updated).GetSemanticModel(updated);
                var diag = comp.GetDiagnostics();
                var linq = new RoslynToExpressionTreeVisitor(comp);
                
                var expr = linq.Visit(k);
                var lambda = Expression.Lambda(expr, linq.Parameters.Values.Concat(linq.Variables.Values));
                var lambdaaa = Nessos.LinqOptimizer.Core.CoreHelpers.ToLambdaExpression(lambda);

                Program.CompileDebuggable(lambdaaa);
                //return SyntaxFactory.CastExpression(semantic.GetTypeInfo(node).ConvertedType.ToString(), SyntaxFactory.LiteralExpression(SyntaxKind.NullKeyword));
                return changed;
            }
            throw new NotImplementedException();
        }

        private int lastId;
        internal List<InvocationExpressionSyntax> InvocationsToReplace;

        private static ExpressionSyntax MakeMemberAccess(ExpressionSyntax b, string name)
        {
            return SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, b, SyntaxFactory.IdentifierName(name));
        }

        private readonly static MethodInfo[] LinqOptimizerMethods = typeof(Nessos.LinqOptimizer.CSharp.Extensions).GetMethods(BindingFlags.Static | BindingFlags.Public);
    }
}
