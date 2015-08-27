using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Reflection;
using Nessos.LinqOptimizer.Base;

namespace CompileSimple2
{
    class RoslynToExpressionTreeVisitor : CSharpSyntaxVisitor<Expression>
    {
        private SemanticModel semantic;
        public RoslynToExpressionTreeVisitor(SemanticModel semantic)
        {
            this.semantic = semantic;
        }


        public override Expression VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var methodExpr = node.Expression as MemberAccessExpressionSyntax;
            if (methodExpr != null)
            {
                var objSymbol = semantic.GetSymbolInfo(methodExpr.Expression).Symbol;
                var obj = objSymbol is ITypeSymbol ? null : Visit(methodExpr.Expression);
                var methodSymbol = (IMethodSymbol)semantic.GetSymbolInfo(methodExpr).Symbol;
                var method = semantic. GetMethod(methodSymbol);
                if (methodSymbol.IsGenericMethod) method = method.MakeGenericMethod(methodSymbol.TypeArguments.Select(x => semantic.GetType(x)).ToArray());
                var parameters = node.ArgumentList.Arguments.Select(x => Visit(x.Expression)).ToArray();
                if (method.IsStatic)
                {
                    if (obj != null)
                    {
                        parameters = new[] { obj }.Concat(parameters).ToArray();
                        obj = null;
                    }
                    return Expression.Call(method, parameters);
                }
                else
                {
                    return Expression.Call(obj, method, parameters);
                }
            }
            throw new NotImplementedException();
        }

        public override Expression VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
        {
            return Expression.Lambda(Visit(node.Body), new[] { (ParameterExpression)Visit(node.Parameter) });
        }
        public override Expression VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
        {
            return Expression.Lambda(Visit(node.Body), node.ParameterList.Parameters.Select(x => (ParameterExpression)Visit(x)).ToArray());
        }

        public override Expression VisitParameter(ParameterSyntax node)
        {
            var sem = semantic.GetDeclaredSymbol(node);
            var type = semantic.GetType(sem.Type);
            return Expression.Parameter(type, node.ToString());
        }
        public override Expression DefaultVisit(SyntaxNode node)
        {
            throw new NotImplementedException();
        }


        public override Expression VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            switch ((SyntaxKind)node.RawKind)
            {
                case SyntaxKind.GreaterThanOrEqualExpression: return Expression.GreaterThanOrEqual(Visit(node.Left), Visit(node.Right));
                case SyntaxKind.GreaterThanExpression: return Expression.GreaterThan(Visit(node.Left), Visit(node.Right));
                case SyntaxKind.LessThanOrEqualExpression: return Expression.LessThanOrEqual(Visit(node.Left), Visit(node.Right));
                case SyntaxKind.LessThanExpression: return Expression.LessThan(Visit(node.Left), Visit(node.Right));
                case SyntaxKind.EqualsExpression: return Expression.Equal(Visit(node.Left), Visit(node.Right));
                case SyntaxKind.NotEqualsExpression: return Expression.NotEqual(Visit(node.Left), Visit(node.Right));
                default: throw new NotImplementedException();
            }
        }

        public override Expression VisitIdentifierName(IdentifierNameSyntax node)
        {
            var symbol = semantic.GetSymbolInfo(node).Symbol;
            var local = symbol as ILocalSymbol;
            if (local != null)
            {
                ParameterExpression expr;

                if (!Variables.TryGetValue(local, out expr))
                {
                    expr = Expression.Variable(semantic.GetType(local.Type), local.Name);
                    Variables.Add(local, expr);
                }
                return expr;
            }
            var parameter = symbol as IParameterSymbol;
            if (parameter != null)
            {
                ParameterExpression expr;

                if (!Parameters.TryGetValue(parameter, out expr))
                {
                    expr = Expression.Parameter(semantic.GetType(parameter.Type), parameter.Name);
                    Parameters.Add(parameter, expr);
                }
                return expr;
            }
            throw new NotImplementedException();
        }

        internal Dictionary<IParameterSymbol, ParameterExpression> Parameters = new Dictionary<IParameterSymbol, ParameterExpression>();
        internal Dictionary<ILocalSymbol, ParameterExpression> Variables = new Dictionary<ILocalSymbol, ParameterExpression>();

        public override Expression VisitLiteralExpression(LiteralExpressionSyntax node)
        {
            var t = semantic.GetType(semantic.GetTypeInfo(node).ConvertedType);
            return Expression.Constant(node.Token.Value, t);
        }
    }
}
