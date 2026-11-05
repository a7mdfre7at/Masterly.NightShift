using System;
using Masterly.NightShift.Common;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Common
{
    public class JobLoadExceptionFacts
    {
        [Fact]
        public void Ctor_CreatesException_WithGivenMessageAnInnerException()
        {
            var innerException = new Exception();
            var exception = new JobLoadException("1", innerException);

            Assert.Equal("1", exception.Message);
            Assert.Same(innerException, exception.InnerException);
        }
    }
}
