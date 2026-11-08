#if NETFRAMEWORK
using Masterly.NightShift.Logging;

namespace Masterly.NightShift.Core.Tests
{
    /// <summary>
    /// On .NET 5 and later a module initializer makes <see cref="LateBoundLogProvider"/> LibLog's current provider
    /// before any logger exists. The netstandard2.0 build has none: the first <c>UseLogProvider</c> call does it, and
    /// loggers created before that call keep the provider LibLog detected. Applications make that call at startup;
    /// the net48 tests do the same here, before any test runs, so their results don't depend on which test runs first.
    /// </summary>
    public sealed class LateBoundLoggingFixture
    {
        public LateBoundLoggingFixture()
        {
            LogProvider.SetCurrentLogProvider(LateBoundLogProvider.Instance);
        }
    }
}
#endif
