using System;
using System.Reflection;
using Moq.Sequences;
using Xunit;
using Xunit.v3;

namespace Masterly.NightShift.Core.Tests
{
    public class SequenceAttribute : BeforeAfterTestAttribute
    {
        private IDisposable _sequence;

        public override void Before(MethodInfo methodUnderTest, IXunitTest test)
        {
            _sequence = Sequence.Create();
        }

        public override void After(MethodInfo methodUnderTest, IXunitTest test)
        {
            _sequence.Dispose();
        }
    }
}
