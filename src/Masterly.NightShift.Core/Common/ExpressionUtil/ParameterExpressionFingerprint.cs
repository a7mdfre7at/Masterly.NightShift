#nullable disable // Ported from ASP.NET MVC and kept as ported
// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace Masterly.NightShift.Common.ExpressionUtil;

// ParameterExpression fingerprint class
// Can represent the model parameter or an inner parameter in an open lambda expression

[ExcludeFromCodeCoverage]
internal sealed class ParameterExpressionFingerprint(ExpressionType nodeType, Type type, int parameterIndex) : ExpressionFingerprint(nodeType, type)
{
    // Parameter position within the overall expression, used to maintain alpha equivalence.
    public int ParameterIndex { get; } = parameterIndex;

    public override bool Equals(object obj)
    {
        return (obj is ParameterExpressionFingerprint other)
               && (ParameterIndex == other.ParameterIndex)
               && Equals(other);
    }

    // The base hashes through AddToHashCodeCombiner, which this class overrides with what Equals compares
    public override int GetHashCode()
    {
        return base.GetHashCode();
    }

    internal override void AddToHashCodeCombiner(HashCodeCombiner combiner)
    {
        combiner.AddInt32(ParameterIndex);
        base.AddToHashCodeCombiner(combiner);
    }
}
