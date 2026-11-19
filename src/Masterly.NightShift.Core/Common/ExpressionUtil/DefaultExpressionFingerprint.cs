#nullable disable // Ported from ASP.NET MVC and kept as ported
// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace Masterly.NightShift.Common.ExpressionUtil;

// DefaultExpression fingerprint class
// Expression of form default(T)

[ExcludeFromCodeCoverage]
internal sealed class DefaultExpressionFingerprint(ExpressionType nodeType, Type type) : ExpressionFingerprint(nodeType, type)
{
    public override bool Equals(object obj)
    {
        return (obj is DefaultExpressionFingerprint other)
               && Equals(other);
    }

    // The base hashes through AddToHashCodeCombiner, which this class overrides with what Equals compares
    public override int GetHashCode()
    {
        return base.GetHashCode();
    }
}
