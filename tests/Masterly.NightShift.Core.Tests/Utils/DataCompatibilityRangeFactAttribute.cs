using System;
using System.Runtime.CompilerServices;
using Xunit;
using Xunit.v3;

namespace Masterly.NightShift.Core.Tests
{
    /// <summary>
    /// A fact that runs once for every <see cref="CompatibilityLevel"/> in the
    /// [<see cref="MinLevel"/>, <see cref="MaxExcludingLevel"/>) range.
    /// </summary>
    [XunitTestCaseDiscoverer(typeof(DataCompatibilityRangeFactDiscoverer))]
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class DataCompatibilityRangeFactAttribute : FactAttribute, IDataCompatibilityRangeAttribute
    {
        public DataCompatibilityRangeFactAttribute(
            [CallerFilePath] string sourceFilePath = null,
            [CallerLineNumber] int sourceLineNumber = -1)
            : base(sourceFilePath, sourceLineNumber)
        {
            MinLevel = DataCompatibilityRange.PossibleMinLevel;
            MaxExcludingLevel = DataCompatibilityRange.PossibleMaxExcludingLevel;
        }

        public CompatibilityLevel MinLevel { get; set; }
        public CompatibilityLevel MaxExcludingLevel { get; set; }
    }
}
