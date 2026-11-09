using System.Runtime.CompilerServices;
using Xunit.v3;

namespace Masterly.NightShift.Core.Tests;

/// <summary>
/// A fact that runs once for every <see cref="CompatibilityLevel"/> in the
/// [<see cref="MinLevel"/>, <see cref="MaxExcludingLevel"/>) range.
/// </summary>
[XunitTestCaseDiscoverer(typeof(DataCompatibilityRangeFactDiscoverer))]
[AttributeUsage(AttributeTargets.Method)]
internal sealed class DataCompatibilityRangeFactAttribute(
    [CallerFilePath] string sourceFilePath = null,
    [CallerLineNumber] int sourceLineNumber = -1) : FactAttribute(sourceFilePath, sourceLineNumber), IDataCompatibilityRangeAttribute
{
    public CompatibilityLevel MinLevel { get; set; } = DataCompatibilityRange.PossibleMinLevel;
    public CompatibilityLevel MaxExcludingLevel { get; set; } = DataCompatibilityRange.PossibleMaxExcludingLevel;
}
