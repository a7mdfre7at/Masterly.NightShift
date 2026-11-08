#nullable enable

using System;
using Masterly.NightShift.Annotations;

namespace Masterly.NightShift.Common
{
    /// <summary>
    /// Marks an assembly as providing generated <see cref="JobInvoker"/> instances. The Masterly.NightShift source
    /// generator applies it to every assembly it emits invokers for; <see cref="JobInvokerRegistry"/> calls
    /// the provider type's static, parameterless <c>Register</c> method when the assembly is loaded.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class JobInvokersProviderAttribute : Attribute
    {
        /// <summary>Name of the static method the provider type must define.</summary>
        public const string RegisterMethodName = "Register";

        /// <summary>
        /// Initializes the attribute.
        /// </summary>
        /// <param name="providerType">A static class with a parameterless static <c>Register</c> method that
        /// calls <see cref="JobInvokerRegistry.Register"/> for each generated invoker.</param>
        public JobInvokersProviderAttribute([NotNull] Type providerType)
        {
            ProviderType = providerType ?? throw new ArgumentNullException(nameof(providerType));
        }

        /// <summary>Gets the type that registers the generated invokers.</summary>
        [NotNull]
        public Type ProviderType { get; }
    }
}
