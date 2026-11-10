namespace Masterly.NightShift.Core.Tests.Common;

public class JobLoadExceptionFacts
{
    [Fact]
    public void Ctor_CreatesException_WithGivenMessageAnInnerException()
    {
        Exception innerException = new();
        JobLoadException exception = new("1", innerException);

        Assert.Equal("1", exception.Message);
        Assert.Same(innerException, exception.InnerException);
    }
}
