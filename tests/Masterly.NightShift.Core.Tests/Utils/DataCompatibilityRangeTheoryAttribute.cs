using System;
using System.Runtime.CompilerServices;
using Xunit;
using Xunit.v3;

namespace Masterly.NightShift.Core.Tests
{
    /// <summary>
    /// A theory whose every data row runs once for every <see cref="CompatibilityLevel"/> in the
    /// [<see cref="MinLevel"/>, <see cref="MaxExcludingLevel"/>) range.
    /// </summary>
    [XunitTestCaseDiscoverer(typeof(DataCompatibilityRangeTheoryDiscoverer))]
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class DataCompatibilityRangeTheoryAttribute : TheoryAttribute, IDataCompatibilityRangeAttribute
    {
        public DataCompatibilityRangeTheoryAttribute(
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
