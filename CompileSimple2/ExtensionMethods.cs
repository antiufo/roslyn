using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace CompileSimple2
{
    static class ExtensionMethods
    {

        public static MethodInfo GetMethod(this SemanticModel semantic, IMethodSymbol methodSymbol)
        {
            var type = GetType(semantic, methodSymbol.ContainingType);
            Type[] parameters;

            parameters = methodSymbol.ConstructedFrom.Parameters.Select(x => GetType(semantic, x.Type)).ToArray();

           if (methodSymbol.ConstructedFrom.IsExtensionMethod && !methodSymbol.ConstructedFrom.ReceiverType.IsStatic)
                parameters = new[] { GetType(semantic, methodSymbol.ConstructedFrom.ReceiverType) }.Concat(parameters).ToArray();
            var z = type.GetMethodExt(methodSymbol.MetadataName, parameters);
            if (z == null) throw new ArgumentException();
            return z;
        }


        public static Type GetType(this SemanticModel semantic, ITypeSymbol symbol)
        {
            return Type.GetType(GetFullMetadataName(semantic, symbol), true);
        }


        public static Assembly GetAssembly(SemanticModel semantic, IAssemblySymbol symbol)
        {
            return Assembly.Load(symbol.Identity.ToString());
        }



        public static string GetFullMetadataName(SemanticModel semantic, INamespaceOrTypeSymbol symbol)
        {

            var arr = symbol as IArrayTypeSymbol;
            var rank = 0;
            if (arr != null)
            {
                symbol = arr.ElementType;
                rank = arr.Rank;
            }

            var tp = symbol as ITypeParameterSymbol;
            if (tp != null)
            {
                return typeof(ReflectionEx.T).AssemblyQualifiedName;
            }

            ISymbol s = symbol;
            var sb = new StringBuilder(GetDecoratedGenericName(semantic, symbol));

            var last = s;
            s = s.ContainingSymbol;
            while (!IsRootNamespace(s))
            {
                if (s is ITypeSymbol && last is ITypeSymbol)
                {
                    sb.Insert(0, '+');
                }
                else
                {
                    sb.Insert(0, '.');
                }

                sb.Insert(0, GetDecoratedGenericName(semantic, s));

                s = s.ContainingSymbol;
            }

            if (rank != 0)
            {
                sb.Append('[');
                for (int i = 0; i < rank - 1; i++)
                {
                    sb.Append(',');
                }
                sb.Append(']');
            }
            sb.Append(", ");
            sb.Append(symbol.ContainingAssembly.Identity.ToString());
            return sb.ToString();
        }

        private static string GetDecoratedGenericName(SemanticModel semantic, ISymbol k)
        {
            var s = k as INamedTypeSymbol;
            if (s != null)
            {
                if (s.Arity == 0) return s.MetadataName;
                return s.MetadataName + "[" + string.Join(",", s.TypeArguments.Select(x => "[" + GetFullMetadataName(semantic, x) + "]")) + "]";
            }
            //var m = k as IMethodSymbol;
            //if (m != null)
            //{
            //    if (m.Arity == 0) return s.MetadataName;
            //    return m.MetadataName + "[" + string.Join(",", m.TypeArguments.Select(x => GetFullMetadataName(x))) + "]";
            //}

            return k.MetadataName;
        }

        private static bool IsRootNamespace(ISymbol s)
        {
            return s is INamespaceSymbol && ((INamespaceSymbol)s).IsGlobalNamespace;
        }
    }
}
