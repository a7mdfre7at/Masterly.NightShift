using System.Reflection;
using System.Threading;
using Xunit.v3;

namespace Hangfire.Core.Tests
{
    internal sealed class GlobalLockAttribute : BeforeAfterTestAttribute
    {
        // Not Monitor: xUnit v3 may call After on a different thread than Before
        private readonly SemaphoreSlim _globalLock = new SemaphoreSlim(1, 1);

        public string Reason { get; set; }

        public override void Before(MethodInfo methodUnderTest, IXunitTest test)
        {
            _globalLock.Wait();
        }

        public override void After(MethodInfo methodUnderTest, IXunitTest test)
        {
            _globalLock.Release();
        }
    }
}