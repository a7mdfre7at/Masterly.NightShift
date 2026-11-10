namespace Masterly.NightShift.Core.Tests.Common;

public class JobFilterAttributeFilterProviderFacts
{
    [Fact]
    public void GetFilters_WithNullJob_ReturnsEmptyList()
    {
        // Arrange
        JobFilterAttributeFilterProvider provider = new();

        // Act
        IEnumerable<JobFilter> result = provider.GetFilters(null);

        // Assert
        Assert.Empty(result);
    }

    [MyFilter(Order = 2112)]
    // ReSharper disable once ClassNeverInstantiated.Local
    private sealed class ClassWithTypeAttribute
    {
        public static void Method() { }
    }

    [Fact]
    public void GetFilters_IncludesAttributesOnClassType()
    {
        // Arrange
        Job job = Job.FromExpression(() => ClassWithTypeAttribute.Method());
        JobFilterAttributeFilterProvider provider = new();

        // Act
        JobFilter filter = provider.GetFilters(job).Single();

        // Assert
        MyFilterAttribute? attribute = filter.Instance as MyFilterAttribute;
        Assert.NotNull(attribute);
        Assert.Equal(JobFilterScope.Type, filter.Scope);
        Assert.Equal(2112, filter.Order);
    }

    // ReSharper disable once ClassNeverInstantiated.Local
    private sealed class ClassWithActionAttribute
    {
        [MyFilter(Order = 1234)]
        public static void Method()
        {
        }
    }

    [Fact]
    public void GetFilters_IncludesAttributesOMethod()
    {
        // Arrange
        Job job = Job.FromExpression(() => ClassWithActionAttribute.Method());
        JobFilterAttributeFilterProvider provider = new();

        // Act
        JobFilter filter = provider.GetFilters(job).Single();

        // Assert
        MyFilterAttribute? attribute = filter.Instance as MyFilterAttribute;
        Assert.NotNull(attribute);
        Assert.Equal(JobFilterScope.Method, filter.Scope);
        Assert.Equal(1234, filter.Order);
    }

    private abstract class BaseClass
    {
        public void MyMethod()
        {
        }
    }

    [MyFilter]
    // ReSharper disable once ClassNeverInstantiated.Local
    private sealed class DerivedClass : BaseClass
    {
    }

    [Fact]
    public void GetFilters_IncludesTypeAttributesFromDerivedTypeWhenMethodIsOnBaseClass()
    {
        // Arrange
        Job job = Job.FromExpression<DerivedClass>(x => x.MyMethod());
        JobFilterAttributeFilterProvider provider = new();

        // Act
        IEnumerable<JobFilter> filters = provider.GetFilters(job);

        // Assert
        Assert.NotNull(filters.Select(f => f.Instance).Cast<MyFilterAttribute>().Single());
    }

    private sealed class MyFilterAttribute : JobFilterAttribute
    {
    }

    [Fact]
    public void GetFilters_RetrievesNonCachedAttributesWhenConfiguredNotTo()
    {
        // Arrange
        Job job = Job.FromExpression<DerivedClass>(x => x.MyMethod());
        JobFilterAttributeFilterProvider provider = new(false);

        // Act
        IEnumerable<JobFilter> filters = provider.GetFilters(job);

        // Assert
        Assert.NotNull(filters.Select(f => f.Instance).Cast<MyFilterAttribute>().Single());
    }
}
