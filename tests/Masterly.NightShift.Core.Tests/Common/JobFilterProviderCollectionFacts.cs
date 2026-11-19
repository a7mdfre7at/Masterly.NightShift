using System.Diagnostics.CodeAnalysis;

namespace Masterly.NightShift.Core.Tests.Common;

public class JobFilterProviderCollectionFacts
{
    private readonly Job _job;

    public JobFilterProviderCollectionFacts()
    {
        _job = Job.FromExpression(() => Sample());
    }

    [Fact]
    public void GetFilters_ReturnsNull_WhenJobIsNull()
    {
        JobFilterProviderCollection collection = [];
        IEnumerable<JobFilter> filters = collection.GetFilters(null);

        Assert.Empty(filters);
    }

    [Fact]
    public void GetFiltersUsesRegisteredProviders()
    {
        // Arrange
        JobFilter filter = new(new object(), JobFilterScope.Method, null);
        Mock<IJobFilterProvider> provider = new(MockBehavior.Strict);
        JobFilterProviderCollection collection = new(provider.Object);
        provider.Setup(p => p.GetFilters(_job)).Returns([filter]);

        // Act
        IEnumerable<JobFilter> result = collection.GetFilters(_job);

        // Assert
        Assert.Same(filter, result.Single());
    }

    [Fact]
    public void GetFiltersSortsFiltersByOrderFirstThenScope()
    {
        // Arrange
        JobFilter actionFilter = new(new object(), JobFilterScope.Method, null);
        JobFilter controllerFilter = new(new object(), JobFilterScope.Type, null);
        JobFilter globalFilter = new(new object(), JobFilterScope.Global, null);
        JobFilter earlyActionFilter = new(new object(), JobFilterScope.Method, -100);
        JobFilter lateGlobalFilter = new(new object(), JobFilterScope.Global, 100);
        Mock<IJobFilterProvider> provider = new(MockBehavior.Strict);
        JobFilterProviderCollection collection = new(provider.Object);
        provider.Setup(p => p.GetFilters(_job))
            .Returns([actionFilter, controllerFilter, globalFilter, earlyActionFilter, lateGlobalFilter]);

        // Act
        JobFilter[] result = [.. collection.GetFilters(_job)];

        // Assert
        Assert.Equal(5, result.Length);
        Assert.Same(earlyActionFilter, result[0]);
        Assert.Same(globalFilter, result[1]);
        Assert.Same(controllerFilter, result[2]);
        Assert.Same(actionFilter, result[3]);
        Assert.Same(lateGlobalFilter, result[4]);
    }

    [Fact]
    public void GetFiltersSortsFiltersStablyAllowMultipleFalse()
    {
        // Arrange
        JobFilter objectFilter = new(new object(), JobFilterScope.Global, null);
        JobFilter allowMultipleFalseAttribute1 = new(new AllowMultipleFalseAttribute(), JobFilterScope.Global, null);
        JobFilter allowMultipleFalseAttribute2 = new(new AllowMultipleFalseAttribute(), JobFilterScope.Global, null);
        Mock<IJobFilterProvider> provider = new(MockBehavior.Strict);
        JobFilterProviderCollection collection = new(provider.Object);

        // to prove instability we need at least 17 items in the list
        provider.Setup(p => p.GetFilters(_job))
            .Returns([
                objectFilter,
                objectFilter,
                objectFilter,
                objectFilter,
                objectFilter,
                objectFilter,
                objectFilter,
                allowMultipleFalseAttribute1,
                objectFilter,
                objectFilter,
                objectFilter,
                allowMultipleFalseAttribute2,
                objectFilter,
                objectFilter,
                objectFilter,
                objectFilter,
                objectFilter
            ]);

        // Act
        IEnumerable<JobFilter> result = collection.GetFilters(_job);

        // Assert
        Assert.Same(allowMultipleFalseAttribute2, result.Last(item => item.Instance is AllowMultipleFalseAttribute));
    }

    [AttributeUsage(AttributeTargets.All)]
    private sealed class AllowMultipleFalseAttribute : JobFilterAttribute
    {
    }

    [Fact]
    public void GetFiltersIncludesLastFilterOnlyWithAttributeUsageAllowMultipleFalse()
    {
        // Arrange
        JobFilter globalFilter = new(new AllowMultipleFalseAttribute(), JobFilterScope.Global, null);
        JobFilter controllerFilter = new(new AllowMultipleFalseAttribute(), JobFilterScope.Type, null);
        JobFilter actionFilter = new(new AllowMultipleFalseAttribute(), JobFilterScope.Method, null);
        Mock<IJobFilterProvider> provider = new(MockBehavior.Strict);
        JobFilterProviderCollection collection = new(provider.Object);
        provider.Setup(p => p.GetFilters(_job))
            .Returns([controllerFilter, actionFilter, globalFilter]);

        // Act
        IEnumerable<JobFilter> result = collection.GetFilters(_job);

        // Assert
        Assert.Same(actionFilter, result.Single());
    }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    private sealed class AllowMultipleTrueAttribute : JobFilterAttribute
    {
    }

    [Fact]
    public void GetFiltersIncludesAllFiltersWithAttributeUsageAllowMultipleTrue()
    {
        // Arrange
        JobFilter globalFilter = new(new AllowMultipleTrueAttribute(), JobFilterScope.Global, null);
        JobFilter controllerFilter = new(new AllowMultipleTrueAttribute(), JobFilterScope.Type, null);
        JobFilter actionFilter = new(new AllowMultipleTrueAttribute(), JobFilterScope.Method, null);
        Mock<IJobFilterProvider> provider = new(MockBehavior.Strict);
        JobFilterProviderCollection collection = new(provider.Object);
        provider.Setup(p => p.GetFilters(_job))
            .Returns([controllerFilter, actionFilter, globalFilter]);

        // Act
        List<JobFilter> result = [.. collection.GetFilters(_job)];

        // Assert
        Assert.Same(globalFilter, result[0]);
        Assert.Same(controllerFilter, result[1]);
        Assert.Same(actionFilter, result[2]);
    }

    private sealed class AllowMultipleCustomFilter(bool allowMultiple) : IJobFilter
    {
        public bool AllowMultiple { get; } = allowMultiple;
        public int Order => -1;
    }

    [Fact]
    public void GetFiltersIncludesLastFilterOnlyWithCustomFilterAllowMultipleFalse()
    {
        // Arrange
        JobFilter globalFilter = new(new AllowMultipleCustomFilter(false), JobFilterScope.Global, null);
        JobFilter controllerFilter = new(new AllowMultipleCustomFilter(false), JobFilterScope.Type, null);
        JobFilter actionFilter = new(new AllowMultipleCustomFilter(false), JobFilterScope.Method, null);
        Mock<IJobFilterProvider> provider = new(MockBehavior.Strict);
        JobFilterProviderCollection collection = new(provider.Object);
        provider.Setup(p => p.GetFilters(_job))
            .Returns([controllerFilter, actionFilter, globalFilter]);

        // Act
        IEnumerable<JobFilter> result = collection.GetFilters(_job);

        // Assert
        Assert.Same(actionFilter, result.Single());
    }

    [Fact]
    public void GetFiltersIncludesAllFiltersWithCustomFilterAllowMultipleTrue()
    {
        // Arrange
        JobFilter globalFilter = new(new AllowMultipleCustomFilter(true), JobFilterScope.Global, null);
        JobFilter controllerFilter = new(new AllowMultipleCustomFilter(true), JobFilterScope.Type, null);
        JobFilter actionFilter = new(new AllowMultipleCustomFilter(true), JobFilterScope.Method, null);
        Mock<IJobFilterProvider> provider = new(MockBehavior.Strict);
        JobFilterProviderCollection collection = new(provider.Object);
        provider.Setup(p => p.GetFilters(_job))
            .Returns([controllerFilter, actionFilter, globalFilter]);

        // Act
        List<JobFilter> result = [.. collection.GetFilters(_job)];

        // Assert
        Assert.Same(globalFilter, result[0]);
        Assert.Same(controllerFilter, result[1]);
        Assert.Same(actionFilter, result[2]);
    }

    [Fact]
    public void GetFilters_ReturnsTheSameList_WhenCalledAgainForTheSameMethod()
    {
        JobFilterCollection global = new() { new CaptureCultureAttribute() };
        JobFilterProviderCollection collection = new(global, new JobFilterAttributeFilterProvider());

        IEnumerable<JobFilter> first = collection.GetFilters(_job);
        IEnumerable<JobFilter> second = collection.GetFilters(_job);

        Assert.Same(first, second);
        Assert.Single(first);
    }

    [Fact]
    public void GetFilters_SeesAFilter_AddedAfterTheFirstCall()
    {
        JobFilterCollection global = new() { new CaptureCultureAttribute() };
        JobFilterProviderCollection collection = new(global, new JobFilterAttributeFilterProvider());
        collection.GetFilters(_job);
        AutomaticRetryAttribute added = new();

        global.Add(added);

        Assert.Contains(collection.GetFilters(_job), x => ReferenceEquals(x.Instance, added));
    }

    [Fact]
    public void GetFilters_NoLongerReturnsAFilter_RemovedAfterTheFirstCall()
    {
        CaptureCultureAttribute removed = new();
        JobFilterCollection global = new() { removed };
        JobFilterProviderCollection collection = new(global, new JobFilterAttributeFilterProvider());
        collection.GetFilters(_job);

        global.Remove(removed);

        Assert.Empty(collection.GetFilters(_job));
    }

    [Fact]
    public void GetFilters_SeesAProvider_AddedAfterTheFirstCall()
    {
        JobFilterCollection global = new() { new CaptureCultureAttribute() };
        JobFilterProviderCollection collection = [global];
        collection.GetFilters(_job);
        JobFilterCollection second = new() { new AutomaticRetryAttribute() };

        collection.Add(second);

        Assert.Equal(2, collection.GetFilters(_job).Count());
    }

    [Fact]
    public void GetFilters_AsksACustomProviderEveryTime()
    {
        Mock<IJobFilterProvider> provider = new();
        provider.Setup(p => p.GetFilters(_job)).Returns([]);
        JobFilterProviderCollection collection = new(provider.Object);

        collection.GetFilters(_job);
        collection.GetFilters(_job);

        provider.Verify(p => p.GetFilters(_job), Times.Exactly(2));
    }

    [Fact]
    public void GetFilters_AsksAProviderWithoutAttributeCachingEveryTime()
    {
        JobFilterProviderCollection collection = new(new JobFilterAttributeFilterProvider(false));

        IEnumerable<JobFilter> first = collection.GetFilters(_job);
        IEnumerable<JobFilter> second = collection.GetFilters(_job);

        Assert.NotSame(first, second);
    }

    [Fact]
    public void GetFilters_KeepsTheFiltersOfDifferentMethodsApart()
    {
        JobFilterProviderCollection collection = new(new JobFilterAttributeFilterProvider());
        Job retried = Job.FromExpression(() => Retried());

        Assert.Empty(collection.GetFilters(_job));
        Assert.Single(collection.GetFilters(retried));
        Assert.Empty(collection.GetFilters(_job));
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    [AutomaticRetry(Attempts = 1)]
    public static void Retried() { }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public static void Sample() { }
}
