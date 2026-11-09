using System.Runtime.CompilerServices;
using Xunit.v3;

namespace Masterly.NightShift.Core.Tests;

/// <summary>
/// A theory whose every data row runs once for every <see cref="CompatibilityLevel"/> in the
/// [<see cref="MinLevel"/>, <see cref="MaxExcludingLevel"/>) range.
/// </summary>
[XunitTestCaseDiscoverer(typeof(DataCompatibilityRangeTheoryDiscoverer))]
[AttributeUsage(AttributeTargets.Method)]
internal sealed class DataCompatibilityRangeTheoryAttribute(
    [CallerFilePath] string sourceFilePath = null,
    [CallerLineNumber] int sourceLineNumber = -1) : TheoryAttribute(sourceFilePath, sourceLineNumber), IDataCompatibilityRangeAttribute
{
    public CompatibilityLevel MinLevel { get; set; } = DataCompatibilityRange.PossibleMinLevel;
    public CompatibilityLevel MaxExcludingLevel { get; set; } = DataCompatibilityRange.PossibleMaxExcludingLevel;
}
