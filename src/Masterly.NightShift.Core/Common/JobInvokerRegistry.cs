#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Logging;

namespace Masterly.NightShift.Common
{
    /// <summary>
    /// Counts of resolved job methods per <see cref="JobInvokerKind"/>.
    /// </summary>
    public sealed class JobInvokerStatistics
    {
        internal JobInvokerStatistics(int registered, int generated, int compiled, int reflection)
        {
            Registered = registered;
            Generated = generated;
            Compiled = compiled;
            Reflection = reflection;
        }

        /// <summary>Gets the number of generated invokers registered, used or not.</summary>
        public int Registered { get; }

        /// <summary>Gets the number of performed methods served by generated invokers.</summary>
        public int Generated { get; }

        /// <summary>Gets the number of performed methods served by runtime-compiled invokers.</summary>
        public int Compiled { get; }

        /// <summary>Gets the number of performed methods served by reflection.</summary>
        public int Reflection { get; }
    }

    /// <summary>
    /// Holds the <see cref="JobInvoker"/> for every background job method that has been performed in this
    /// process, and the generated invokers registered by assemblies compiled with the Masterly.NightShift source generator.
    /// </summary>
    /// <remarks>
    /// <para>Resolution order for a method is: a registered generated invoker, then a
    /// <see cref="JobInvokerKind.Compiled"/> invoker when <see cref="UseCompiledInvokers"/> is on and the runtime
    /// can compile expression trees, then <see cref="JobInvokerKind.Reflection"/>. The result is cached per
    /// <see cref="MethodInfo"/>.</para>
    /// <para>Generated invokers register themselves through a module initializer where the target framework
    /// supports one. Assemblies that can't (for example .NET Framework consumers of the <c>netstandard2.0</c>
    /// build) are found by their <see cref="JobInvokersProviderAttribute"/> the first time an invoker is needed,
    /// and whenever a new assembly is loaded afterwards.</para>
    /// </remarks>
    public static class JobInvokerRegistry
    {
        private static readonly ILog Logger = LogProvider.GetLogger(typeof(JobInvokerRegistry));

        private static readonly ConcurrentDictionary<JobMethodSignature, JobInvoker> Generated = new();
        private static readonly ConcurrentDictionary<MethodInfo, JobInvoker> Resolved = new();

        // MethodInfo instances for the same method differ by ReflectedType, so fallbacks are shared by signature
        private static readonly ConcurrentDictionary<JobMethodSignature, JobInvoker> Fallbacks = new();
        private static readonly ConcurrentDictionary<Assembly, bool> ScannedAssemblies = new();

        private static int _generatedCount;
        private static int _compiledCount;
        private static int _reflectionCount;
        private static int _assemblyScanInitialized;

        /// <summary>
        /// Gets or sets whether methods without a generated invoker get a <see cref="JobInvokerKind.Compiled"/>
        /// invoker instead of reflection. On by default. Changing it doesn't affect methods already resolved.
        /// </summary>
        public static bool UseCompiledInvokers { get; set; } = true;

        /// <summary>
        /// Lists every registered generated invoker: the methods the source generator found in the loaded
        /// assemblies. The dashboard's Job Catalog is built from it. Assemblies not scanned yet are scanned first.
        /// </summary>
        public static IReadOnlyList<JobInvoker> GetGeneratedInvokers()
        {
            EnsureAssembliesScanned();

            var result = new List<JobInvoker>(Generated.Count);
            foreach (var invoker in Generated.Values)
            {
                result.Add(invoker);
            }

            result.Sort(static (x, y) =>
            {
                var byType = String.CompareOrdinal(x.Type.FullName, y.Type.FullName);
                return byType != 0 ? byType : String.CompareOrdinal(x.MethodName, y.MethodName);
            });

            return result;
        }

        /// <summary>
        /// Registers a generated invoker. Later registrations for the same method win, so a newer build of
        /// an assembly loaded into the same process replaces an older one. Called by generated code.
        /// </summary>
        /// <param name="invoker">The invoker to register.</param>
        public static void Register([NotNull] JobInvoker invoker)
        {
            if (invoker == null) throw new ArgumentNullException(nameof(invoker));

            var signature = new JobMethodSignature(invoker.Type, invoker.MethodName, invoker.ParameterTypes, invoker.GenericArguments);
            Generated[signature] = invoker;
        }

        /// <summary>
        /// Gets the invoker for a method. Never returns <c>null</c>: reflection is the last resort.
        /// </summary>
        /// <param name="method">A closed, public method.</param>
        /// <exception cref="ArgumentException"><paramref name="method"/> is a global method, which no invoker can call.</exception>
        public static JobInvoker GetInvoker([NotNull] MethodInfo method)
        {
            if (method == null) throw new ArgumentNullException(nameof(method));

            if (Resolved.TryGetValue(method, out var invoker))
            {
                return invoker;
            }

            EnsureAssembliesScanned();

            invoker = Resolved.GetOrAdd(method, static m => Resolve(m));
            return invoker;
        }

        /// <summary>
        /// Gets the generated invoker for a method, if one was registered.
        /// </summary>
        /// <param name="method">A closed, public method.</param>
        /// <param name="invoker">The generated invoker, or <c>null</c>.</param>
        public static bool TryGetGeneratedInvoker([NotNull] MethodInfo method, out JobInvoker? invoker)
        {
            if (method == null) throw new ArgumentNullException(nameof(method));

            EnsureAssembliesScanned();

            if (method.DeclaringType == null)
            {
                invoker = null;
                return false;
            }

            var signature = JobMethodSignature.FromMethod(method);
            return Generated.TryGetValue(signature, out invoker);
        }

        /// <summary>
        /// Gets how many methods were resolved to each kind of invoker so far.
        /// </summary>
        public static JobInvokerStatistics GetStatistics()
        {
            return new JobInvokerStatistics(
                Generated.Count,
                Volatile.Read(ref _generatedCount),
                Volatile.Read(ref _compiledCount),
                Volatile.Read(ref _reflectionCount));
        }

        /// <summary>
        /// Registers every generated invoker the assembly provides through its
        /// <see cref="JobInvokersProviderAttribute"/>. Safe to call more than once per assembly.
        /// </summary>
        /// <param name="assembly">An assembly compiled with the Masterly.NightShift source generator.</param>
        public static void RegisterAssembly([NotNull] Assembly assembly)
        {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));

            if (!ScannedAssemblies.TryAdd(assembly, true)) return;
            if (assembly.IsDynamic) return;

            try
            {
                foreach (var provider in assembly.GetCustomAttributes<JobInvokersProviderAttribute>())
                {
                    var register = provider.ProviderType.GetTypeInfo()
                        .GetDeclaredMethod(JobInvokersProviderAttribute.RegisterMethodName);

                    if (register == null || !register.IsStatic || register.GetParameters().Length != 0)
                    {
                        Logger.Warn($"Type `{provider.ProviderType}` is marked as a job invokers provider, but has no parameterless static `{JobInvokersProviderAttribute.RegisterMethodName}` method.");
                        continue;
                    }

                    register.Invoke(null, null);
                }
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                // An assembly that can't be inspected only costs us its generated invokers.
                Logger.DebugException($"Unable to register generated job invokers from assembly `{assembly.FullName}`.", ex);
            }
        }

        /// <summary>
        /// Forgets every resolved invoker, so the next performance resolves them again. Generated invokers
        /// stay registered. Intended for tests.
        /// </summary>
        public static void ClearResolved()
        {
            Resolved.Clear();
            Fallbacks.Clear();
            Interlocked.Exchange(ref _generatedCount, 0);
            Interlocked.Exchange(ref _compiledCount, 0);
            Interlocked.Exchange(ref _reflectionCount, 0);
        }

        private static JobInvoker Resolve(MethodInfo method)
        {
            if (method.DeclaringType == null)
            {
                // No invoker can call a global method, so don't count one as resolved
                throw new ArgumentException("Global methods are not supported.", nameof(method));
            }

            var signature = JobMethodSignature.FromMethod(method);

            if (Generated.TryGetValue(signature, out var generated))
            {
                Interlocked.Increment(ref _generatedCount);
                return generated;
            }

            return Fallbacks.GetOrAdd(signature, _ => CreateFallback(method));
        }

        private static JobInvoker CreateFallback(MethodInfo method)
        {
            if (UseCompiledInvokers && CompiledJobInvoker.IsSupported)
            {
                var compiled = CompiledJobInvoker.TryCreate(method);
                if (compiled != null)
                {
                    Interlocked.Increment(ref _compiledCount);
                    return compiled;
                }
            }

            Interlocked.Increment(ref _reflectionCount);
            return new ReflectionJobInvoker(method);
        }

        private static void EnsureAssembliesScanned()
        {
            if (Volatile.Read(ref _assemblyScanInitialized) != 0) return;

            if (Interlocked.CompareExchange(ref _assemblyScanInitialized, 1, 0) == 0)
            {
                AppDomain.CurrentDomain.AssemblyLoad += static (_, args) => RegisterAssembly(args.LoadedAssembly);
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                RegisterAssembly(assembly);
            }
        }

        private readonly struct JobMethodSignature : IEquatable<JobMethodSignature>
        {
            private readonly Type _type;
            private readonly string _name;
            private readonly Type[] _parameterTypes;
            private readonly Type[] _genericArguments;

            public JobMethodSignature(Type type, string name, Type[] parameterTypes, Type[] genericArguments)
            {
                _type = type;
                _name = name;
                _parameterTypes = parameterTypes;
                _genericArguments = genericArguments;
            }

            public static JobMethodSignature FromMethod(MethodInfo method)
            {
                return new JobMethodSignature(
                    method.DeclaringType!,
                    method.Name,
                    method.GetParameters().Select(static x => x.ParameterType).ToArray(),
                    method.IsGenericMethod ? method.GetGenericArguments() : Type.EmptyTypes);
            }

            public bool Equals(JobMethodSignature other)
            {
                return _type == other._type
                    && String.Equals(_name, other._name, StringComparison.Ordinal)
                    && SequenceEqual(_parameterTypes, other._parameterTypes)
                    && SequenceEqual(_genericArguments, other._genericArguments);
            }

            public override bool Equals(object? obj)
            {
                return obj is JobMethodSignature other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = _type.GetHashCode();
                    hash = hash * 31 + StringComparer.Ordinal.GetHashCode(_name);
                    hash = hash * 31 + _parameterTypes.Length;

                    foreach (var parameterType in _parameterTypes)
                    {
                        hash = hash * 31 + parameterType.GetHashCode();
                    }

                    foreach (var genericArgument in _genericArguments)
                    {
                        hash = hash * 31 + genericArgument.GetHashCode();
                    }

                    return hash;
                }
            }

            private static bool SequenceEqual(IReadOnlyList<Type> left, IReadOnlyList<Type> right)
            {
                if (left.Count != right.Count) return false;

                for (var i = 0; i < left.Count; i++)
                {
                    if (left[i] != right[i]) return false;
                }

                return true;
            }
        }
    }
}
