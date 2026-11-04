using System.Collections.Generic;
using System.Threading.Tasks;
using Hangfire.Annotations;
using Xunit.Sdk;
using Xunit.v3;

namespace Hangfire.Core.Tests
{
    /// <summary>
    /// Lets xUnit's own <see cref="FactDiscoverer"/> create the test case, then repeats it per compatibility level.
    /// </summary>
    [UsedImplicitly]
    internal sealed class DataCompatibilityRangeFactDiscoverer : FactDiscoverer
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
