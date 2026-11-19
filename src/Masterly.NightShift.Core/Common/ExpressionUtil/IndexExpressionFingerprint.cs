#nullable disable // Ported from ASP.NET MVC and kept as ported
// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace Masterly.NightShift.Common.ExpressionUtil;

// IndexExpression fingerprint class
// Represents certain forms of array access or indexer property access

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

    // The base hashes through AddToHashCodeCombiner, which this class overrides with what Equals compares
    public override int GetHashCode()
    {
        return base.GetHashCode();
    }

    internal override void AddToHashCodeCombiner(HashCodeCombiner combiner)
    {
        combiner.AddObject(Indexer);
        base.AddToHashCodeCombiner(combiner);
    }
}
