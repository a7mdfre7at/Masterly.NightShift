#nullable disable // Ported from ASP.NET MVC and kept as ported
// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace Masterly.NightShift.Common.ExpressionUtil;

// MemberExpression fingerprint class
// Expression of form xxx.FieldOrProperty

[ExcludeFromCodeCoverage]
internal sealed class MemberExpressionFingerprint(ExpressionType nodeType, Type type, MemberInfo member) : ExpressionFingerprint(nodeType, type)
{
    // http://msdn.microsoft.com/en-us/library/system.linq.expressions.memberexpression.member.aspx
    public MemberInfo Member { get; } = member;

    public override bool Equals(object obj)
    {
        return (obj is MemberExpressionFingerprint other)
               && Equals(Member, other.Member)
               && Equals(other);
    }

    // The base hashes through AddToHashCodeCombiner, which this class overrides with what Equals compares
    public override int GetHashCode()
    {
        return base.GetHashCode();
    }

    internal override void AddToHashCodeCombiner(HashCodeCombiner combiner)
    {
        combiner.AddObject(Member);
        base.AddToHashCodeCombiner(combiner);
    }
}
