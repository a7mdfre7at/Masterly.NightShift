// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

#pragma warning disable 659 // overrides AddToHashCodeCombiner instead

namespace Masterly.NightShift.Common.ExpressionUtil;

// LambdaExpression fingerprint class
// Represents a lambda expression (root element in Expression<T>)

[SuppressMessage("Microsoft.Usage", "CA2218:OverrideGetHashCodeOnOverridingEquals", Justification = "Overrides AddToHashCodeCombiner() instead.")]
[SuppressMessage("SonarLint", "S1206:OverrideGetHashCodeOnOverridingEquals", Justification = "Overrides AddToHashCodeCombiner() instead.")]
[ExcludeFromCodeCoverage]
internal sealed class LambdaExpressionFingerprint(ExpressionType nodeType, Type type) : ExpressionFingerprint(nodeType, type)
{
    public override bool Equals(object obj)
    {
        return (obj is LambdaExpressionFingerprint other)
               && Equals(other);
    }
}
