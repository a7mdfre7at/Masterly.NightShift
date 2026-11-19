#nullable disable // Ported from ASP.NET MVC and kept as ported
// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace Masterly.NightShift.Common.ExpressionUtil;

// TypeBinary fingerprint class
// Expression of form "obj is T"

[ExcludeFromCodeCoverage]
internal sealed class TypeBinaryExpressionFingerprint(ExpressionType nodeType, Type type, Type typeOperand) : ExpressionFingerprint(nodeType, type)
{
    // http://msdn.microsoft.com/en-us/library/system.linq.expressions.typebinaryexpression.typeoperand.aspx
    public Type TypeOperand { get; } = typeOperand;

    public override bool Equals(object obj)
    {
        return (obj is TypeBinaryExpressionFingerprint other)
               && TypeOperand == other.TypeOperand
               && Equals(other);
    }

    // The base hashes through AddToHashCodeCombiner, which this class overrides with what Equals compares
    public override int GetHashCode()
    {
        return base.GetHashCode();
    }

    internal override void AddToHashCodeCombiner(HashCodeCombiner combiner)
    {
        combiner.AddObject(TypeOperand);
        base.AddToHashCodeCombiner(combiner);
    }
}
