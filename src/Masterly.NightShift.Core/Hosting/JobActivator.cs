// This file is part of Hangfire. Copyright © 2013-2014 Hangfire OÜ.
// 
// Hangfire is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as 
// published by the Free Software Foundation, either version 3 
// of the License, or any later version.
// 
// Hangfire is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
// 
// You should have received a copy of the GNU Lesser General Public 
// License along with Hangfire. If not, see <http://www.gnu.org/licenses/>.

using Masterly.NightShift.Server;

namespace Masterly.NightShift;

/// <summary>
/// Creates the instances that jobs run on. The default implementation calls the type's parameterless
/// constructor; <c>Masterly.NightShift.NetCore</c> provides one that resolves them from the
/// <see cref="IServiceProvider"/>. Set it with <c>UseActivator</c> or <see cref="Current"/>, or per server
/// with <see cref="BackgroundJobServerOptions.Activator"/>.
/// </summary>
public class JobActivator
{
    /// <summary>
    /// Gets or sets the current <see cref="JobActivator"/> instance 
    /// that will be used to activate jobs during performance.
    /// </summary>
    public static JobActivator Current
    {
        get;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            field = value;
        }
    } = new();

    /// <summary>
    /// Creates an instance of <paramref name="jobType"/>. The default implementation calls its public
    /// parameterless constructor.
    /// </summary>
    /// <param name="jobType">The type that declares the job method.</param>
    /// <returns>The instance; the server fails the job when it is <c>null</c>.</returns>
    public virtual object? ActivateJob([NotNull] Type jobType)
    {
        return Activator.CreateInstance(jobType);
    }

    /// <summary>
    /// Begins the scope a job's instance is resolved from, and that is disposed when the job has run.
    /// The default implementation returns a scope that creates instances with <see cref="ActivateJob"/> and
    /// disposes those that implement <see cref="IDisposable"/>.
    /// </summary>
    /// <param name="context">The job about to run.</param>
    public virtual JobActivatorScope BeginScope([NotNull] JobActivatorContext context)
    {
        return new SimpleJobActivatorScope(this);
    }

    /// <summary>
    /// Begins the scope for a job performed by a server, by calling <see cref="BeginScope(JobActivatorContext)"/>.
    /// </summary>
    /// <param name="context">The job about to run.</param>
    public virtual JobActivatorScope BeginScope([NotNull] PerformContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return this.BeginScope(new JobActivatorContext(context.Connection, context.BackgroundJob, context.CancellationToken));
    }

    private class SimpleJobActivatorScope : JobActivatorScope
    {
        private readonly JobActivator _activator;
        private readonly List<IDisposable> _disposables = [];

        public SimpleJobActivatorScope([NotNull] JobActivator activator)
        {
            ArgumentNullException.ThrowIfNull(activator);
            _activator = activator;
        }

        public override object? Resolve(Type type)
        {
            object? instance = _activator.ActivateJob(type);

            if (instance is IDisposable disposable)
            {
                _disposables.Add(disposable);
            }

            return instance;
        }

        public override void DisposeScope()
        {
            foreach (IDisposable disposable in _disposables)
            {
                disposable.Dispose();
            }
        }
    }
}
