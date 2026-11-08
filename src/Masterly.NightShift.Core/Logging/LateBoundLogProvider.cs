#nullable enable

using System;
using System.Threading;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;

namespace Masterly.NightShift.Logging
{
    /// <summary>
    /// The provider Masterly.NightShift's loggers are created from. Its loggers look up the provider chosen with
    /// <c>UseLogProvider</c> each time they write, instead of keeping the one that was current when they were
    /// created. So a logger created early, for example by the default global filters when <c>GlobalJobFilters</c>
    /// is first used, still writes to a provider set later by <c>AddNightShift</c> or <c>Use...LogProvider()</c>.
    /// </summary>
    /// <remarks>
    /// On .NET 5 and later a module initializer makes it LibLog's current provider before any logger exists.
    /// The netstandard2.0 build can't have one (the attribute would need a polyfill that consumers' source
    /// generators would see), so there it becomes the current provider on the first <c>UseLogProvider</c> call,
    /// and only loggers created after that call follow later changes.
    /// </remarks>
    internal sealed class LateBoundLogProvider : ILogProvider
    {
        public static readonly LateBoundLogProvider Instance = new LateBoundLogProvider();

        private static readonly Lazy<ILogProvider> DetectedProvider = new Lazy<ILogProvider>(
            DetectProvider,
            LazyThreadSafetyMode.ExecutionAndPublication);

        private static ILogProvider? _target;

        private LateBoundLogProvider()
        {
        }

        /// <summary>The provider set with <c>UseLogProvider</c>, or <c>null</c> to detect one like LibLog does.</summary>
        [CanBeNull]
        public static ILogProvider? Target
        {
            get => Volatile.Read(ref _target);
            set => Volatile.Write(ref _target, value is LateBoundLogProvider ? null : value);
        }

        /// <summary>Makes <paramref name="provider"/> the target of every Masterly.NightShift logger, old and new.</summary>
        public static void Use([NotNull] ILogProvider provider)
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));

            Target = provider;

            // Also covers the netstandard2.0 build, and code that replaced the provider with LogProvider.SetCurrentLogProvider
            LogProvider.SetCurrentLogProvider(Instance);
        }

#if NET5_0_OR_GREATER
        // A library rarely needs a module initializer, but nothing else runs before the first logger is created
#pragma warning disable CA2255 // The 'ModuleInitializer' attribute should not be used in libraries
        [System.Runtime.CompilerServices.ModuleInitializer]
#pragma warning restore CA2255
        internal static void Install()
        {
            LogProvider.SetCurrentLogProvider(Instance);
        }
#endif

        /// <inheritdoc />
        public ILog GetLogger(string name)
        {
            return new LateBoundLog(name);
        }

        private static ILogProvider GetCurrentProvider()
        {
            return Volatile.Read(ref _target) ?? DetectedProvider.Value;
        }

        private static ILogProvider DetectProvider()
        {
            // The same libraries, in the same order, as LibLog's own detection, which never runs while this
            // provider is the current one
            try
            {
                foreach (var resolver in LogProvider.LogProviderResolvers)
                {
                    if (resolver.Item1())
                    {
                        return resolver.Item2();
                    }
                }
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                // Like LibLog: there is no logger to report this to
                Console.WriteLine($"Exception occurred resolving a log provider. Logging for Masterly.NightShift is disabled. {ex}");
            }

            return LogProvider.NoOpLogProvider.Instance;
        }

        private sealed class LateBoundLog : ILog
        {
            private readonly string _name;
            private Binding? _binding;

            public LateBoundLog(string name)
            {
                _name = name;
            }

            public bool Log(LogLevel logLevel, Func<string>? messageFunc, Exception? exception = null)
            {
                var provider = GetCurrentProvider();
                var binding = Volatile.Read(ref _binding);

                if (binding == null || !ReferenceEquals(binding.Provider, provider))
                {
                    ILog logger;
                    try
                    {
                        logger = provider.GetLogger(_name) ?? LogProvider.NoOpLogger.Instance;
                    }
                    catch (Exception ex) when (ex.IsCatchableExceptionType())
                    {
                        // For example an ILoggerFactory disposed while the host shuts down. A log call must not
                        // fail the code that makes it, and there is nowhere to report this; the next call tries again.
                        return false;
                    }

                    binding = new Binding(provider, logger);
                    Volatile.Write(ref _binding, binding);
                }

                return binding.Logger.Log(logLevel, messageFunc, exception);
            }
        }

        private sealed class Binding
        {
            public Binding(ILogProvider provider, ILog logger)
            {
                Provider = provider;
                Logger = logger;
            }

            public ILogProvider Provider { get; }
            public ILog Logger { get; }
        }
    }
}
