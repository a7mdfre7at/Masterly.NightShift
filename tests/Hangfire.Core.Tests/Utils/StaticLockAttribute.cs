using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using Xunit.v3;

namespace Hangfire.Core.Tests
{
    internal sealed class StaticLockAttribute : BeforeAfterTestAttribute
    {
        // Not Monitor: xUnit v3 may call After on a different thread than Before
        private readonly ConcurrentDictionary<Type, SemaphoreSlim> _locks
            = new ConcurrentDictionary<Type, SemaphoreSlim>();

        public override void Before(MethodInfo methodUnderTest, IXunitTest test)
        {
            _locks.GetOrAdd(GetType(methodUnderTest), _ => new SemaphoreSlim(1, 1)).Wait();
        }

        public override void After(MethodInfo methodUnderTest, IXunitTest test)
        {
            _locks[GetType(methodUnderTest)].Release();
        }

        private static Type GetType(MethodInfo methodInfo)
        {
            return methodInfo.DeclaringType;
        }
    }
}
