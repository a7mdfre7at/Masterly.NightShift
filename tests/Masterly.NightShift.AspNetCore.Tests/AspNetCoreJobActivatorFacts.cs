using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Masterly.NightShift.AspNetCore.Tests
{
    public class AspNetCoreJobActivatorFacts
    {
        [Fact]
        public void Ctor_ThrowsAnException_WhenScopeFactoryIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(() => new AspNetCoreJobActivator(null));

            Assert.Equal("serviceScopeFactory", exception.ParamName);
        }

        [Fact]
        public void BeginScope_ResolvesScopedServices_OncePerScope()
        {
            using (var provider = BuildProvider())
            {
                var activator = new AspNetCoreJobActivator(provider.GetRequiredService<IServiceScopeFactory>());
                using (var scope = activator.BeginScope((JobActivatorContext)null))
                {
                    var first = scope.Resolve(typeof(AsyncOnlyDisposable));
                    var second = scope.Resolve(typeof(AsyncOnlyDisposable));

                    Assert.Same(first, second);
                }
            }
        }

        [Fact]
        public void BeginScope_CreatesUnregisteredTypes()
        {
            using (var provider = BuildProvider())
            {
                var activator = new AspNetCoreJobActivator(provider.GetRequiredService<IServiceScopeFactory>());
                using (var scope = activator.BeginScope((JobActivatorContext)null))
                {
                    var instance = scope.Resolve(typeof(UnregisteredJob));

                    Assert.IsType<UnregisteredJob>(instance);
                }
            }
        }

        [Fact]
        public void DisposeScope_DisposesServices_ThatAreOnlyAsyncDisposable()
        {
            // A synchronous scope disposal throws for such services, so this only passes on the asynchronous path
            using (var provider = BuildProvider())
            {
                var activator = new AspNetCoreJobActivator(provider.GetRequiredService<IServiceScopeFactory>());
                var scope = activator.BeginScope((JobActivatorContext)null);
                var instance = (AsyncOnlyDisposable)scope.Resolve(typeof(AsyncOnlyDisposable));

                scope.Dispose();

                Assert.True(instance.Disposed);
            }
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
}
