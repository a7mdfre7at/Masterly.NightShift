using System.Reflection;

namespace Masterly.NightShift.Core.Tests.Common;

public class MethodInfoExtensionsFacts
{
    [Fact]
    public void GetNormalizedName_ReturnsNormalizedName_ForRegularMethod()
    {
        var service = new RegularInterfaceImplementation();
        var normalizedName = service.GetType().GetRuntimeMethods().First().GetNormalizedName();

        Assert.Equal("Method", normalizedName);
    }

    [Fact]
    public void GetNormalizedName_ReturnsNormalizedName_ForExplicitlyImplementedMethod()
    {
        var service = new ExplicitInterfaceImplementation();
        var normalizedName = service.GetType().GetRuntimeMethods().First().GetNormalizedName();

        Assert.Equal("Method", normalizedName);
    }

    private interface IService
    {
        void Method();
    }

    private sealed class RegularInterfaceImplementation : IService
    {
        public void Method()
        {
        }
    }

    private sealed class ExplicitInterfaceImplementation : IService
    {
        void IService.Method()
        {
        }
    }
}
