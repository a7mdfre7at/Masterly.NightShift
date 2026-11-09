// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

#pragma warning disable 659 // overrides AddToHashCodeCombiner instead

namespace Masterly.NightShift.Common.ExpressionUtil;

// IndexExpression fingerprint class
// Represents certain forms of array access or indexer property access

[SuppressMessage("Microsoft.Usage", "CA2218:OverrideGetHashCodeOnOverridingEquals", Justification = "Overrides AddToHashCodeCombiner() instead.")]
[SuppressMessage("SonarLint", "S1206:OverrideGetHashCodeOnOverridingEquals", Justification = "Overrides AddToHashCodeCombiner() instead.")]
[ExcludeFromCodeCoverage]
internal sealed class IndexExpressionFingerprint(ExpressionType nodeType, Type type, PropertyInfo indexer) : ExpressionFingerprint(nodeType, type)
{

    // http://msdn.microsoft.com/en-us/library/system.linq.expressions.indexexpression.indexer.aspx
    public PropertyInfo Indexer { get; } = indexer;

    public override bool Equals(object obj)
    {
        return (obj is IndexExpressionFingerprint other)
               && Equals(Indexer, other.Indexer)
               && Equals(other);
    }

    internal override void AddToHashCodeCombiner(HashCodeCombiner combiner)
    {
        combiner.AddObject(Indexer);
        base.AddToHashCodeCombiner(combiner);
    }
}
