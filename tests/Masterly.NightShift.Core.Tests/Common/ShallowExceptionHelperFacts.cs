using System;
using Masterly.NightShift.Common;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Common
{
    public class ShallowExceptionHelperFacts
    {
        [Fact]
        public void PreserveOriginalStackTrace_CanBeCalledTwice_WithoutThrowingAnyException()
        {
            try
            {
                throw new InvalidOperationException("Hello, world!");
            }
            catch (Exception ex)
            {
                ex.PreserveOriginalStackTrace();
                ex.PreserveOriginalStackTrace();
            }
        }
    }
}