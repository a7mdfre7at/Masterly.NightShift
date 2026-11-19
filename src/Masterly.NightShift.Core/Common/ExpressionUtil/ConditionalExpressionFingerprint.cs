#nullable disable // Ported from ASP.NET MVC and kept as ported
// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace Masterly.NightShift.Common.ExpressionUtil;

// ConditionalExpression fingerprint class
// Expression of form (test) ? ifTrue : ifFalse

[ExcludeFromCodeCoverage]
internal sealed class ConditionalExpressionFingerprint(ExpressionType nodeType, Type type) : ExpressionFingerprint(nodeType, type)
{
    public override bool Equals(object obj)
    {
        return (obj is ConditionalExpressionFingerprint other)
               && Equals(other);
    }

    // The base hashes through AddToHashCodeCombiner, which this class overrides with what Equals compares
    public override int GetHashCode()
    {
        return base.GetHashCode();
    }
}
