#nullable disable // Ported from ASP.NET MVC and kept as ported
// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace Masterly.NightShift.Common.ExpressionUtil;

// ConstantExpression fingerprint class
//
// A ConstantExpression might represent a captured local variable, so we can't compile
// the value directly into the cached function. Instead, a placeholder is generated
// and the value is hoisted into a local variables array. This placeholder can then
// be compiled and cached, and the array lookup happens at runtime.

[ExcludeFromCodeCoverage]
internal sealed class ConstantExpressionFingerprint(ExpressionType nodeType, Type type) : ExpressionFingerprint(nodeType, type)
{
    public override bool Equals(object obj)
    {
        return (obj is ConstantExpressionFingerprint other)
               && Equals(other);
    }

    // The base hashes through AddToHashCodeCombiner, which this class overrides with what Equals compares
    public override int GetHashCode()
    {
        return base.GetHashCode();
    }
}
