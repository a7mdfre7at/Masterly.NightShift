// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

#pragma warning disable 659 // overrides AddToHashCodeCombiner instead

namespace Masterly.NightShift.Common.ExpressionUtil;

// UnaryExpression fingerprint class
// The most common appearance of a UnaryExpression is a cast or other conversion operator

[SuppressMessage("Microsoft.Usage", "CA2218:OverrideGetHashCodeOnOverridingEquals", Justification = "Overrides AddToHashCodeCombiner() instead.")]
[SuppressMessage("SonarLint", "S1206:OverrideGetHashCodeOnOverridingEquals", Justification = "Overrides AddToHashCodeCombiner() instead.")]
[ExcludeFromCodeCoverage]
internal sealed class UnaryExpressionFingerprint(ExpressionType nodeType, Type type, MethodInfo method) : ExpressionFingerprint(nodeType, type)
{

    // http://msdn.microsoft.com/en-us/library/system.linq.expressions.unaryexpression.method.aspx
    public MethodInfo Method { get; } = method;

    public override bool Equals(object obj)
    {
        return (obj is UnaryExpressionFingerprint other)
               && Equals(Method, other.Method)
               && Equals(other);
    }

    internal override void AddToHashCodeCombiner(HashCodeCombiner combiner)
    {
        combiner.AddObject(Method);
        base.AddToHashCodeCombiner(combiner);
    }
}
