using System;
using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Common;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Common
{
    public class ReflectionJobInvokerFacts
    {
        [Fact]
        public void Ctor_DescribesTheMethod_AsAReflectionInvoker()
        {
            var invoker = new ReflectionJobInvoker(typeof(Jobs).GetMethod(nameof(Jobs.Concat)));

            Assert.Equal(JobInvokerKind.Reflection, invoker.Kind);
            Assert.Equal(typeof(Jobs), invoker.Type);
            Assert.Equal(new[] { typeof(string), typeof(int) }, invoker.ParameterTypes);
        }

        [Fact]
        public void Invoke_ThrowsAnException_WhenArgumentsAreNull()
        {
            var invoker = new ReflectionJobInvoker(typeof(Jobs).GetMethod(nameof(Jobs.Concat)));

            var exception = Assert.Throws<ArgumentNullException>(() => invoker.Invoke(new Jobs(), null));

            Assert.Equal("arguments", exception.ParamName);
        }

        [Fact]
        public void Invoke_CallsInstanceMethods_AndReturnsTheResult()
        {
            var invoker = new ReflectionJobInvoker(typeof(Jobs).GetMethod(nameof(Jobs.Concat)));

            var result = invoker.Invoke(new Jobs(), new object[] { "a", 1 });

            Assert.Equal("a1", result);
        }

        [Fact]
        public void Invoke_CallsStaticMethods_AndReturnsNullForVoid()
        {
            var invoker = new ReflectionJobInvoker(typeof(Jobs).GetMethod(nameof(Jobs.StaticVoid)));

            var result = invoker.Invoke(null, new object[0]);

            Assert.Null(result);
        }

        [Fact]
        public void Invoke_ThrowsTheJobsOwnException_InsteadOfTargetInvocationException()
        {
            var invoker = new ReflectionJobInvoker(typeof(Jobs).GetMethod(nameof(Jobs.Throw)));

            var exception = Assert.Throws<InvalidOperationException>(() => invoker.Invoke(new Jobs(), new object[0]));

            Assert.Equal("boom", exception.Message);
            Assert.Contains(nameof(Jobs.Throw), exception.StackTrace);
        }

        [Fact]
        public void Invoke_LetsArgumentMismatchesThrough_Unwrapped()
        {
            var invoker = new ReflectionJobInvoker(typeof(Jobs).GetMethod(nameof(Jobs.Concat)));

            Assert.Throws<ArgumentException>(() => invoker.Invoke(new Jobs(), new object[] { 1, "a" }));
        }

        [SuppressMessage("ReSharper", "MemberCanBeMadeStatic.Global")]
        [SuppressMessage("Performance", "CA1822:Mark members as static")]
        public class Jobs
        {
            public string Concat(string text, int number) => text + number;

            public static void StaticVoid()
            {
            }

            public void Throw() => throw new InvalidOperationException("boom");
        }
    }
}
