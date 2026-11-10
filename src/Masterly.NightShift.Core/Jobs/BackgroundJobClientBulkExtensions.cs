using System.Linq.Expressions;

namespace Masterly.NightShift;

/// <summary>
/// Creates many background jobs at once. The jobs are written in a few storage round trips instead of two per
/// job, so this is much faster than calling <c>Enqueue</c> in a loop.
/// </summary>
/// <remarks>
/// <para>Client filters run for every job, as with <c>Enqueue</c>: <c>OnCreating</c> for each job first, then
/// the jobs are created, then <c>OnCreated</c> for each job.</para>
/// <para>Jobs are created in groups of <see cref="BackgroundJobClient.BulkSize"/>. A group is created as a
/// whole or not at all; when a group fails, the groups before it stay created and the exception is thrown.</para>
/// </remarks>
public static class BackgroundJobClientBulkExtensions
{
    /// <param name="client">A job client instance.</param>
    extension([NotNull] IBackgroundJobClient client)
    {
        /// <summary>
        /// Creates the given jobs and places each into its queue.
        /// </summary>
        /// <param name="jobs">The jobs to create.</param>
        /// <returns>The identifier of each job, in the order given; <c>null</c> for a job a filter canceled.</returns>
        public IReadOnlyList<string?> EnqueueMany([NotNull, InstantHandle] IEnumerable<Job> jobs)
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(jobs);

            IReadOnlyList<Job> list = jobs as IReadOnlyList<Job> ?? [.. jobs];

            if (client is BackgroundJobClient bulkClient)
            {
                return bulkClient.CreateMany(list, static () => new EnqueuedState());
            }

            // Another implementation of the interface: one job at a time
            string?[] ids = new string?[list.Count];
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = client.Create(list[i], new EnqueuedState());
            }

            return ids;
        }

        /// <summary>
        /// Creates one job per method call and places each into its queue.
        /// </summary>
        /// <typeparam name="T">The type whose method is called.</typeparam>
        /// <param name="methodCalls">The method calls, one per job.</param>
        /// <returns>The identifier of each job, in the order given; <c>null</c> for a job a filter canceled.</returns>
        public IReadOnlyList<string?> EnqueueMany<T>([NotNull, InstantHandle] IEnumerable<Expression<Action<T>>> methodCalls)
        {
            ArgumentNullException.ThrowIfNull(methodCalls);
            return client.EnqueueMany(methodCalls.Select(static x => Job.FromExpression(x)));
        }

        /// <summary>
        /// Creates one job per asynchronous method call and places each into its queue.
        /// </summary>
        /// <typeparam name="T">The type whose method is called.</typeparam>
        /// <param name="methodCalls">The method calls, one per job.</param>
        /// <returns>The identifier of each job, in the order given; <c>null</c> for a job a filter canceled.</returns>
        public IReadOnlyList<string?> EnqueueMany<T>([NotNull, InstantHandle] IEnumerable<Expression<Func<T, Task>>> methodCalls)
        {
            ArgumentNullException.ThrowIfNull(methodCalls);
            return client.EnqueueMany(methodCalls.Select(static x => Job.FromExpression(x)));
        }

        /// <summary>
        /// Creates one job per item: <paramref name="methodCall"/> is the call to make, and its second parameter
        /// stands for the item. For example
        /// <c>jobs.EnqueueMany&lt;EmailService, int&gt;(userIds, (x, id) => x.SendWelcome(id))</c>.
        /// </summary>
        /// <typeparam name="T">The type whose method is called.</typeparam>
        /// <typeparam name="TItem">The type of the items.</typeparam>
        /// <param name="items">One item per job.</param>
        /// <param name="methodCall">The method call, written once for every item.</param>
        /// <returns>The identifier of each job, in the order given; <c>null</c> for a job a filter canceled.</returns>
        public IReadOnlyList<string?> EnqueueMany<T, TItem>(
            [NotNull, InstantHandle] IEnumerable<TItem> items,
            [NotNull, InstantHandle] Expression<Action<T, TItem>> methodCall)
        {
            ArgumentNullException.ThrowIfNull(items);
            ArgumentNullException.ThrowIfNull(methodCall);

            return client.EnqueueMany(items.Select(item => Job.FromExpression(BindItem<Action<T>, TItem>(methodCall, item))));
        }

        /// <summary>
        /// Creates one job per item: <paramref name="methodCall"/> is the asynchronous call to make, and its
        /// second parameter stands for the item. For example
        /// <c>jobs.EnqueueMany&lt;EmailService, int&gt;(userIds, (x, id) => x.SendWelcomeAsync(id))</c>.
        /// </summary>
        /// <typeparam name="T">The type whose method is called.</typeparam>
        /// <typeparam name="TItem">The type of the items.</typeparam>
        /// <param name="items">One item per job.</param>
        /// <param name="methodCall">The method call, written once for every item.</param>
        /// <returns>The identifier of each job, in the order given; <c>null</c> for a job a filter canceled.</returns>
        public IReadOnlyList<string?> EnqueueMany<T, TItem>(
            [NotNull, InstantHandle] IEnumerable<TItem> items,
            [NotNull, InstantHandle] Expression<Func<T, TItem, Task>> methodCall)
        {
            ArgumentNullException.ThrowIfNull(items);
            ArgumentNullException.ThrowIfNull(methodCall);

            return client.EnqueueMany(items.Select(item => Job.FromExpression(BindItem<Func<T, Task>, TItem>(methodCall, item))));
        }
    }

    // Turns (x, item) => x.Method(item) into x => x.Method(<the item>)
    private static Expression<TDelegate> BindItem<TDelegate, TItem>(LambdaExpression methodCall, TItem item)
    {
        ParameterExpression itemParameter = methodCall.Parameters[1];
        Expression body = new ReplaceParameter(itemParameter, Expression.Constant(item, typeof(TItem))).Visit(methodCall.Body);
        return Expression.Lambda<TDelegate>(body, methodCall.Parameters[0]);
    }

    private sealed class ReplaceParameter(ParameterExpression parameter, Expression replacement) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
        {
            return node == parameter ? replacement : node;
        }
    }
}
