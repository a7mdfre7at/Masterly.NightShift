namespace Masterly.NightShift.AspNetCore.Tests;

public class AspNetCoreJobActivatorFacts
{
    [Fact]
    public void Ctor_ThrowsAnException_WhenScopeFactoryIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => new AspNetCoreJobActivator(null));

        Assert.Equal("serviceScopeFactory", exception.ParamName);
    }

    [Fact]
    public void BeginScope_ResolvesScopedServices_OncePerScope()
    {
        using ServiceProvider provider = BuildProvider();
        AspNetCoreJobActivator activator = new(provider.GetRequiredService<IServiceScopeFactory>());
        using JobActivatorScope scope = activator.BeginScope((JobActivatorContext)null);
        object? first = scope.Resolve(typeof(AsyncOnlyDisposable));
        object? second = scope.Resolve(typeof(AsyncOnlyDisposable));

        Assert.Same(first, second);
    }

    [Fact]
    public void BeginScope_CreatesUnregisteredTypes()
    {
        using ServiceProvider provider = BuildProvider();
        AspNetCoreJobActivator activator = new(provider.GetRequiredService<IServiceScopeFactory>());
        using JobActivatorScope scope = activator.BeginScope((JobActivatorContext)null);
        object? instance = scope.Resolve(typeof(UnregisteredJob));

        Assert.IsType<UnregisteredJob>(instance);
    }

    [Fact]
    public void DisposeScope_DisposesServices_ThatAreOnlyAsyncDisposable()
    {
        // A synchronous scope disposal throws for such services, so this only passes on the asynchronous path
        using ServiceProvider provider = BuildProvider();
        AspNetCoreJobActivator activator = new(provider.GetRequiredService<IServiceScopeFactory>());
        AsyncOnlyDisposable instance;
        using (JobActivatorScope scope = activator.BeginScope((JobActivatorContext)null))
        {
            instance = (AsyncOnlyDisposable)scope.Resolve(typeof(AsyncOnlyDisposable));
        }

        Assert.True(instance.Disposed);
    }

    private static ServiceProvider BuildProvider()
    {
        return new ServiceCollection()
            .AddScoped<AsyncOnlyDisposable>()
            .BuildServiceProvider();
    }

    public sealed class AsyncOnlyDisposable : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return default;
        }
    }

    public sealed class UnregisteredJob
    {
    }
}
