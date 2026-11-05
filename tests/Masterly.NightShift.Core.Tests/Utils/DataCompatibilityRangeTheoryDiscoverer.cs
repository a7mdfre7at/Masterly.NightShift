using System.Collections.Generic;
using System.Threading.Tasks;
using Masterly.NightShift.Annotations;
using Xunit.Sdk;
using Xunit.v3;

namespace Masterly.NightShift.Core.Tests
{
    /// <summary>
    /// Lets xUnit's own <see cref="TheoryDiscoverer"/> create the test cases (one per data row, or a single
    /// delay-enumerated one), then repeats each of them per compatibility level.
    /// </summary>
    [UsedImplicitly]
    internal sealed class DataCompatibilityRangeTheoryDiscoverer : TheoryDiscoverer
    {
        public override async ValueTask<IReadOnlyCollection<IXunitTestCase>> Discover(
            ITestFrameworkDiscoveryOptions discoveryOptions,
            IXunitTestMethod testMethod,
            IFactAttribute factAttribute)
        {
            var testCases = await base.Discover(discoveryOptions, testMethod, factAttribute);
            return DataCompatibilityRange.Expand(testCases, (IDataCompatibilityRangeAttribute)factAttribute);
        }
    }
}
