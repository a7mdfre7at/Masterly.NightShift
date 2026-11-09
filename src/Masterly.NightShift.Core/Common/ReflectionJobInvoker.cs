using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Masterly.NightShift.Common;

/// <summary>
/// The last-resort <see cref="JobInvoker"/>: <see cref="MethodBase.Invoke(object, object[])"/>, with
/// the <see cref="TargetInvocationException"/> unwrapped so callers see the job's own exception.
/// </summary>
internal sealed class ReflectionJobInvoker(MethodInfo method) : JobInvoker(method, JobInvokerKind.Reflection)
{
    public override object? Invoke(object? instance, object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        try
        {
            return Method.Invoke(instance, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
