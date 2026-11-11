using System.Linq.Expressions;

namespace Masterly.NightShift;

public sealed partial class JobChain
{
    /// <summary>Starts a chain with its first step.</summary>
    /// <param name="methodCall">The method call of the step.</param>
    /// <param name="queue">The queue of the step, or <c>null</c> for the one its method's attributes give. Needs a storage that keeps a job's queue.</param>
    /// <returns>A new chain.</returns>
    public static JobChain StartWith([NotNull, InstantHandle] Expression<Action> methodCall, [CanBeNull] string? queue = null)
    {
        return Start(Job.FromExpression(methodCall, queue));
    }

    /// <inheritdoc cref="StartWith(Expression{Action}, string)"/>
    public static JobChain StartWith([NotNull, InstantHandle] Expression<Func<Task>> methodCall, [CanBeNull] string? queue = null)
    {
        return Start(Job.FromExpression(methodCall, queue));
    }

    /// <inheritdoc cref="StartWith(Expression{Action}, string)"/>
    public static JobChain StartWith<T>([NotNull, InstantHandle] Expression<Action<T>> methodCall, [CanBeNull] string? queue = null)
    {
        return Start(Job.FromExpression(methodCall, queue));
    }

    /// <inheritdoc cref="StartWith(Expression{Action}, string)"/>
    public static JobChain StartWith<T>([NotNull, InstantHandle] Expression<Func<T, Task>> methodCall, [CanBeNull] string? queue = null)
    {
        return Start(Job.FromExpression(methodCall, queue));
    }

    /// <summary>Adds a step after the level before it: a continuation of a single step, or of every branch of a group.</summary>
    /// <param name="methodCall">The method call of the step.</param>
    /// <param name="options">Which outcomes of the level before start the step: by default, when it succeeded.</param>
    /// <param name="queue">The queue of the step, or <c>null</c> for the one its method's attributes give. Needs a storage that keeps a job's queue.</param>
    /// <returns>The same chain, for chaining.</returns>
    public JobChain Then([NotNull, InstantHandle] Expression<Action> methodCall, JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState, [CanBeNull] string? queue = null)
    {
        return AddThen(Job.FromExpression(methodCall, queue), options);
    }

    /// <inheritdoc cref="Then(Expression{Action}, JobContinuationOptions, string)"/>
    public JobChain Then([NotNull, InstantHandle] Expression<Func<Task>> methodCall, JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState, [CanBeNull] string? queue = null)
    {
        return AddThen(Job.FromExpression(methodCall, queue), options);
    }

    /// <inheritdoc cref="Then(Expression{Action}, JobContinuationOptions, string)"/>
    public JobChain Then<T>([NotNull, InstantHandle] Expression<Action<T>> methodCall, JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState, [CanBeNull] string? queue = null)
    {
        return AddThen(Job.FromExpression(methodCall, queue), options);
    }

    /// <inheritdoc cref="Then(Expression{Action}, JobContinuationOptions, string)"/>
    public JobChain Then<T>([NotNull, InstantHandle] Expression<Func<T, Task>> methodCall, JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState, [CanBeNull] string? queue = null)
    {
        return AddThen(Job.FromExpression(methodCall, queue), options);
    }

    /// <summary>Adds a join that starts once every step of the level before succeeded, and is skipped otherwise. Consecutive joins wait for the same level.</summary>
    /// <param name="methodCall">The method call of the step.</param>
    /// <param name="queue">The queue of the step, or <c>null</c> for the one its method's attributes give. Needs a storage that keeps a job's queue.</param>
    /// <returns>The same chain, for chaining.</returns>
    public JobChain ThenAfterAll([NotNull, InstantHandle] Expression<Action> methodCall, [CanBeNull] string? queue = null)
    {
        return AddJoin(Job.FromExpression(methodCall, queue), ChainLink.AfterAll);
    }

    /// <inheritdoc cref="ThenAfterAll(Expression{Action}, string)"/>
    public JobChain ThenAfterAll([NotNull, InstantHandle] Expression<Func<Task>> methodCall, [CanBeNull] string? queue = null)
    {
        return AddJoin(Job.FromExpression(methodCall, queue), ChainLink.AfterAll);
    }

    /// <inheritdoc cref="ThenAfterAll(Expression{Action}, string)"/>
    public JobChain ThenAfterAll<T>([NotNull, InstantHandle] Expression<Action<T>> methodCall, [CanBeNull] string? queue = null)
    {
        return AddJoin(Job.FromExpression(methodCall, queue), ChainLink.AfterAll);
    }

    /// <inheritdoc cref="ThenAfterAll(Expression{Action}, string)"/>
    public JobChain ThenAfterAll<T>([NotNull, InstantHandle] Expression<Func<T, Task>> methodCall, [CanBeNull] string? queue = null)
    {
        return AddJoin(Job.FromExpression(methodCall, queue), ChainLink.AfterAll);
    }

    /// <summary>Adds a join that starts as soon as one step of the level before succeeded, and is skipped when none did. Consecutive joins wait for the same level.</summary>
    /// <param name="methodCall">The method call of the step.</param>
    /// <param name="queue">The queue of the step, or <c>null</c> for the one its method's attributes give. Needs a storage that keeps a job's queue.</param>
    /// <returns>The same chain, for chaining.</returns>
    public JobChain ThenAfterAny([NotNull, InstantHandle] Expression<Action> methodCall, [CanBeNull] string? queue = null)
    {
        return AddJoin(Job.FromExpression(methodCall, queue), ChainLink.AfterAny);
    }

    /// <inheritdoc cref="ThenAfterAny(Expression{Action}, string)"/>
    public JobChain ThenAfterAny([NotNull, InstantHandle] Expression<Func<Task>> methodCall, [CanBeNull] string? queue = null)
    {
        return AddJoin(Job.FromExpression(methodCall, queue), ChainLink.AfterAny);
    }

    /// <inheritdoc cref="ThenAfterAny(Expression{Action}, string)"/>
    public JobChain ThenAfterAny<T>([NotNull, InstantHandle] Expression<Action<T>> methodCall, [CanBeNull] string? queue = null)
    {
        return AddJoin(Job.FromExpression(methodCall, queue), ChainLink.AfterAny);
    }

    /// <inheritdoc cref="ThenAfterAny(Expression{Action}, string)"/>
    public JobChain ThenAfterAny<T>([NotNull, InstantHandle] Expression<Func<T, Task>> methodCall, [CanBeNull] string? queue = null)
    {
        return AddJoin(Job.FromExpression(methodCall, queue), ChainLink.AfterAny);
    }

    /// <summary>Adds a join that starts as soon as one step of the level before failed for good, and is skipped when none did. Consecutive joins wait for the same level.</summary>
    /// <param name="methodCall">The method call of the step.</param>
    /// <param name="queue">The queue of the step, or <c>null</c> for the one its method's attributes give. Needs a storage that keeps a job's queue.</param>
    /// <returns>The same chain, for chaining.</returns>
    public JobChain ThenAfterAnyFailed([NotNull, InstantHandle] Expression<Action> methodCall, [CanBeNull] string? queue = null)
    {
        return AddJoin(Job.FromExpression(methodCall, queue), ChainLink.AfterAnyFailed);
    }

    /// <inheritdoc cref="ThenAfterAnyFailed(Expression{Action}, string)"/>
    public JobChain ThenAfterAnyFailed([NotNull, InstantHandle] Expression<Func<Task>> methodCall, [CanBeNull] string? queue = null)
    {
        return AddJoin(Job.FromExpression(methodCall, queue), ChainLink.AfterAnyFailed);
    }

    /// <inheritdoc cref="ThenAfterAnyFailed(Expression{Action}, string)"/>
    public JobChain ThenAfterAnyFailed<T>([NotNull, InstantHandle] Expression<Action<T>> methodCall, [CanBeNull] string? queue = null)
    {
        return AddJoin(Job.FromExpression(methodCall, queue), ChainLink.AfterAnyFailed);
    }

    /// <inheritdoc cref="ThenAfterAnyFailed(Expression{Action}, string)"/>
    public JobChain ThenAfterAnyFailed<T>([NotNull, InstantHandle] Expression<Func<T, Task>> methodCall, [CanBeNull] string? queue = null)
    {
        return AddJoin(Job.FromExpression(methodCall, queue), ChainLink.AfterAnyFailed);
    }

    /// <summary>Adds a join that starts as soon as one step of the level before failed for good or was deleted, and is skipped when every step succeeded. Consecutive joins wait for the same level.</summary>
    /// <param name="methodCall">The method call of the step.</param>
    /// <param name="queue">The queue of the step, or <c>null</c> for the one its method's attributes give. Needs a storage that keeps a job's queue.</param>
    /// <returns>The same chain, for chaining.</returns>
    public JobChain ThenAfterAnyFailedOrCanceled([NotNull, InstantHandle] Expression<Action> methodCall, [CanBeNull] string? queue = null)
    {
        return AddJoin(Job.FromExpression(methodCall, queue), ChainLink.AfterAnyFailedOrCanceled);
    }

    /// <inheritdoc cref="ThenAfterAnyFailedOrCanceled(Expression{Action}, string)"/>
    public JobChain ThenAfterAnyFailedOrCanceled([NotNull, InstantHandle] Expression<Func<Task>> methodCall, [CanBeNull] string? queue = null)
    {
        return AddJoin(Job.FromExpression(methodCall, queue), ChainLink.AfterAnyFailedOrCanceled);
    }

    /// <inheritdoc cref="ThenAfterAnyFailedOrCanceled(Expression{Action}, string)"/>
    public JobChain ThenAfterAnyFailedOrCanceled<T>([NotNull, InstantHandle] Expression<Action<T>> methodCall, [CanBeNull] string? queue = null)
    {
        return AddJoin(Job.FromExpression(methodCall, queue), ChainLink.AfterAnyFailedOrCanceled);
    }

    /// <inheritdoc cref="ThenAfterAnyFailedOrCanceled(Expression{Action}, string)"/>
    public JobChain ThenAfterAnyFailedOrCanceled<T>([NotNull, InstantHandle] Expression<Func<T, Task>> methodCall, [CanBeNull] string? queue = null)
    {
        return AddJoin(Job.FromExpression(methodCall, queue), ChainLink.AfterAnyFailedOrCanceled);
    }

    /// <summary>Adds a join that starts once every step of the level before finished, whatever the outcome. Consecutive joins wait for the same level.</summary>
    /// <param name="methodCall">The method call of the step.</param>
    /// <param name="queue">The queue of the step, or <c>null</c> for the one its method's attributes give. Needs a storage that keeps a job's queue.</param>
    /// <returns>The same chain, for chaining.</returns>
    public JobChain ThenAfterAllCompleted([NotNull, InstantHandle] Expression<Action> methodCall, [CanBeNull] string? queue = null)
    {
        return AddJoin(Job.FromExpression(methodCall, queue), ChainLink.AfterAllCompleted);
    }

    /// <inheritdoc cref="ThenAfterAllCompleted(Expression{Action}, string)"/>
    public JobChain ThenAfterAllCompleted([NotNull, InstantHandle] Expression<Func<Task>> methodCall, [CanBeNull] string? queue = null)
    {
        return AddJoin(Job.FromExpression(methodCall, queue), ChainLink.AfterAllCompleted);
    }

    /// <inheritdoc cref="ThenAfterAllCompleted(Expression{Action}, string)"/>
    public JobChain ThenAfterAllCompleted<T>([NotNull, InstantHandle] Expression<Action<T>> methodCall, [CanBeNull] string? queue = null)
    {
        return AddJoin(Job.FromExpression(methodCall, queue), ChainLink.AfterAllCompleted);
    }

    /// <inheritdoc cref="ThenAfterAllCompleted(Expression{Action}, string)"/>
    public JobChain ThenAfterAllCompleted<T>([NotNull, InstantHandle] Expression<Func<T, Task>> methodCall, [CanBeNull] string? queue = null)
    {
        return AddJoin(Job.FromExpression(methodCall, queue), ChainLink.AfterAllCompleted);
    }
}

public sealed partial class JobChainBranches
{
    /// <summary>Adds a branch, which continues the level before the group.</summary>
    /// <param name="methodCall">The method call of the step.</param>
    /// <param name="options">Which outcomes of the level before start the step: by default, when it succeeded.</param>
    /// <param name="queue">The queue of the step, or <c>null</c> for the one its method's attributes give. Needs a storage that keeps a job's queue.</param>
    /// <returns>The same group, for chaining.</returns>
    public JobChainBranches Branch([NotNull, InstantHandle] Expression<Action> methodCall, JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState, [CanBeNull] string? queue = null)
    {
        return AddBranch(Job.FromExpression(methodCall, queue), options);
    }

    /// <inheritdoc cref="Branch(Expression{Action}, JobContinuationOptions, string)"/>
    public JobChainBranches Branch([NotNull, InstantHandle] Expression<Func<Task>> methodCall, JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState, [CanBeNull] string? queue = null)
    {
        return AddBranch(Job.FromExpression(methodCall, queue), options);
    }

    /// <inheritdoc cref="Branch(Expression{Action}, JobContinuationOptions, string)"/>
    public JobChainBranches Branch<T>([NotNull, InstantHandle] Expression<Action<T>> methodCall, JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState, [CanBeNull] string? queue = null)
    {
        return AddBranch(Job.FromExpression(methodCall, queue), options);
    }

    /// <inheritdoc cref="Branch(Expression{Action}, JobContinuationOptions, string)"/>
    public JobChainBranches Branch<T>([NotNull, InstantHandle] Expression<Func<T, Task>> methodCall, JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState, [CanBeNull] string? queue = null)
    {
        return AddBranch(Job.FromExpression(methodCall, queue), options);
    }
}
