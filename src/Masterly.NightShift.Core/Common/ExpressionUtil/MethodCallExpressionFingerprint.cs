#nullable disable // Ported from ASP.NET MVC and kept as ported
// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace Masterly.NightShift.Common.ExpressionUtil;

// MethodCallExpression fingerprint class
// Expression of form xxx.Foo(...), xxx[...] (get_Item()), etc.

[ExcludeFromCodeCoverage]
internal sealed class MethodCallExpressionFingerprint(ExpressionType nodeType, Type type, MethodInfo method) : ExpressionFingerprint(nodeType, type)
{
    // http://msdn.microsoft.com/en-us/library/system.linq.expressions.methodcallexpression.method.aspx
    public MethodInfo Method { get; } = method;

    public override bool Equals(object obj)
    {
        return (obj is MethodCallExpressionFingerprint other)
               && Equals(Method, other.Method)
               && Equals(other);
    }

    // The base hashes through AddToHashCodeCombiner, which this class overrides with what Equals compares
    public override int GetHashCode()
    {
        return base.GetHashCode();
    }

    internal override void AddToHashCodeCombiner(HashCodeCombiner combiner)
    {
        combiner.AddObject(Method);
        base.AddToHashCodeCombiner(combiner);
    }
}
