using System;
using Masterly.NightShift.Annotations;
using Xunit.Sdk;
using Xunit.v3;

namespace Masterly.NightShift.Core.Tests
{
    /// <summary>
    /// A fact, or a single theory data row, that runs under one <see cref="CompatibilityLevel"/>.
    /// </summary>
    internal sealed class DataCompatibilityRangeTestCase : XunitTestCase
    {
        [Obsolete("Called by the de-serializer; should only be called by deriving classes for de-serialization purposes")]
        [UsedImplicitly]
        public DataCompatibilityRangeTestCase()
        {
        }

        public DataCompatibilityRangeTestCase(CompatibilityLevel level, XunitTestCase source)
            : base(
                source.TestMethod,
                DataCompatibilityRange.GetDisplayName(source.TestCaseDisplayName, level),
                DataCompatibilityRange.GetUniqueId(source.UniqueID, level),
                source.Explicit,
                source.TestLabel,
                source.DisableParallelization,
                source.SkipExceptions,
                source.SkipReason,
                source.SkipType,
                source.SkipUnless,
                source.SkipWhen,
                source.Traits,
                source.TestMethodArguments,
                source.SourceFilePath,
                source.SourceLineNumber,
                source.Timeout)
        {
            Level = level;
        }

        public CompatibilityLevel Level { get; private set; }

        public override void PreInvoke()
        {
            base.PreInvoke();
            DataCompatibilityRange.Enter(Level);
        }

        public override void PostInvoke()
        {
            DataCompatibilityRange.Exit();
            base.PostInvoke();
        }

        protected override void Serialize(IXunitSerializationInfo info)
        {
            base.Serialize(info);
            DataCompatibilityRange.Serialize(info, Level);
        }

        protected override void Deserialize(IXunitSerializationInfo info)
        {
            base.Deserialize(info);
            Level = DataCompatibilityRange.Deserialize(info);
        }
    }
}
