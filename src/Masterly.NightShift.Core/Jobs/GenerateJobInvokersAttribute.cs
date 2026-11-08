using System;

namespace Masterly.NightShift
{
    /// <summary>
    /// Asks the Masterly.NightShift source generator to emit a reflection-free <see cref="Common.JobInvoker"/> for every
    /// public method of the type, so jobs can be performed without reflection even when they are created in
    /// another assembly. Methods used in <c>Enqueue</c>, <c>Schedule</c>, <c>AddOrUpdate</c> and similar calls
    /// in the same assembly get invokers automatically and don't need this attribute.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Struct, Inherited = false)]
    public sealed class GenerateJobInvokersAttribute : Attribute
    {
    }
}
