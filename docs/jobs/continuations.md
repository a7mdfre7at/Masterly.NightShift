# Continuations

A continuation is a job that waits for another job (its parent) to finish, then starts. Use it to build simple workflows: charge the card, then ship, then send the receipt; or run clean-up code only when a job was deleted. To wait for several parents at once, see [Fan-in and batches](fan-in-and-batches.md).

## Quick example

```csharp
using Masterly.NightShift;

public class OrderService
{
    public Task ChargeAsync(int orderId) => Task.CompletedTask;
}

public class EmailService
{
    public void SendReceipt(int orderId)
    {
        // send the receipt
    }
}

string? chargeId = jobs.Enqueue<OrderService>(x => x.ChargeAsync(orderId));
if (chargeId != null)
{
    jobs.ContinueJobWith<EmailService>(chargeId, x => x.SendReceipt(orderId));
}
```

`jobs` is an `IBackgroundJobClient`. With the static facade, call `BackgroundJob.ContinueJobWith<EmailService>(chargeId, x => x.SendReceipt(orderId))`.

## How it works

1. `ContinueJobWith` creates the child job in the `Awaiting` state (`AwaitingState`). The state records the parent ID, the state to move to when the condition is met (`NextState`, `Enqueued` by default) and the `JobContinuationOptions`.
2. The `ContinuationsSupportAttribute` filter, registered globally by default, adds the child's ID to the parent's `Continuations` job parameter. It takes a distributed lock on the parent while it does so. If the parent does not exist, creation fails with an exception.
3. If the parent has already finished, the child is decided right away. Otherwise it waits.
4. When the parent moves to `Succeeded` or `Deleted`, the filter looks at every waiting child. A child whose options accept that outcome moves to its next state; the others move to `Deleted` with the reason "Continuation condition was not met".

`Failed` is not a finished state. A parent that failed and is out of retries keeps its children waiting until someone requeues it (then it can succeed) or deletes it.

Continuations appear on the dashboard's Awaiting jobs page, and the job details page draws the chain of parents and children.

## API

### ContinueJobWith

Every overload returns `string?`: the child's ID, or `null` when a client filter canceled the creation. `parentId` must be the ID of an existing job.

Extension methods on `IBackgroundJobClient`:

| Overload | Lambda types | Defaults |
|---|---|---|
| `ContinueJobWith(parentId, methodCall)` | `Action`, `Action<T>` | Next state `Enqueued`, `OnlyOnSucceededState` |
| `ContinueJobWith(parentId, methodCall, JobContinuationOptions options)` | `Action`, `Action<T>` | Next state `Enqueued` |
| `ContinueJobWith(parentId, methodCall, IState nextState)` | `Action`, `Action<T>` | `OnlyOnSucceededState` |
| `ContinueJobWith(parentId, methodCall, IState nextState, JobContinuationOptions options)` | `Action`, `Action<T>` | None |
| `ContinueJobWith(parentId, methodCall, IState? nextState = null, JobContinuationOptions options = OnlyOnSucceededState)` | `Func<Task>`, `Func<T, Task>` | Next state `Enqueued` when `null` |
| `ContinueJobWith(parentId, string queue, methodCall, IState? nextState = null, JobContinuationOptions options = OnlyOnSucceededState)` | all four | Next state `Enqueued` when `null`; `queue` is stored with the child |

Static methods on `BackgroundJob`:

| Overload | Lambda types |
|---|---|
| `ContinueJobWith(parentId, methodCall)` | `Action`, `Action<T>` |
| `ContinueJobWith(parentId, methodCall, JobContinuationOptions options)` | `Action`, `Action<T>` |
| `ContinueJobWith(parentId, methodCall, JobContinuationOptions options = OnlyOnSucceededState)` | `Func<Task>`, `Func<T, Task>` |
| `ContinueJobWith(parentId, string queue, methodCall, JobContinuationOptions options = OnlyOnSucceededState)` | all four |

The static facade has no `nextState` overloads; use `IBackgroundJobClient` for those. The older `ContinueWith` methods still exist with the same arguments but are obsolete.

### JobContinuationOptions

A `[Flags]` enum that says which outcomes of the parent start the child.

| Value | Number | The child starts when the parent is |
|---|---|---|
| `OnlyOnSucceededState` | 1 | `Succeeded` (the default) |
| `OnlyOnDeletedState` | 2 | `Deleted` |
| `OnAnyFinishedState` | 0 | `Succeeded` or `Deleted` |

`OnlyOnSucceededState | OnlyOnDeletedState` means the same as `OnAnyFinishedState`. In every other case the child is deleted when the parent finishes.

### AwaitingState

| Member | Type | Meaning |
|---|---|---|
| `AwaitingState(string parentId)` | constructor | Next state `Enqueued`, `OnAnyFinishedState` |
| `AwaitingState(string parentId, IState nextState)` | constructor | `OnAnyFinishedState` |
| `AwaitingState(string parentId, IState nextState, JobContinuationOptions options)` | constructor | |
| `ParentId` | `string` | The parent job |
| `NextState` | `IState` | The state the child moves to when the condition is met |
| `Options` | `JobContinuationOptions` | Which outcomes count |
| `StateName` | `string` | `"Awaiting"` |

The constructors without `options` default to `OnAnyFinishedState`, as they always have, unlike `ContinueJobWith`, which defaults to `OnlyOnSucceededState`. Pass the options when you create an `AwaitingState` yourself.

### Passing the parent's result or exception

A continuation can receive what its parent returned, or the exception that deleted it. Turn the feature on once at startup:

```csharp
services.AddNightShift(config => config
    .UseResultsInContinuations()
    .UseSqlServerStorage(connectionString));
```

`UseResultsInContinuations()` replaces the default continuation filter with one that copies the parent's outcome into the child's job parameters, and adds `JobParameterInjectionFilter`, which fills marked method parameters from job parameters just before the job runs.

| Attribute | Fills the parameter with | Parameter type |
|---|---|---|
| `[FromResult]` | The parent's return value (job parameter `AntecedentResult`), when the parent succeeded | The parent's return type, or a type its JSON can be read as |
| `[FromException]` | The exception that deleted the parent (job parameter `AntecedentException`). When the parent was deleted without an exception (by `Delete`, from the dashboard), an `OperationCanceledException` | `ExceptionInfo` (has `Type`, `Message`, `InnerException`) |
| `[FromParameter("name")]` | The value of the child's own job parameter `name`, read with your serializer settings | Any type the value can be read as |

When the job parameter is missing, the parameter keeps the value given in the lambda, so pass a placeholder such as `default` or `null` there.

## More examples

### A chain of steps

```csharp
string? charge = jobs.Enqueue<OrderService>(x => x.ChargeAsync(orderId));
string? ship = charge == null ? null : jobs.ContinueJobWith<OrderService>(charge, x => x.ShipAsync(orderId));
if (ship != null)
{
    jobs.ContinueJobWith<EmailService>(ship, x => x.SendReceipt(orderId));
}
```

If the charge is deleted, the shipping job is deleted with "Continuation condition was not met", and the receipt job after it is deleted too.

### Compensation when a job is deleted

```csharp
jobs.ContinueJobWith<OrderService>(
    chargeId,
    x => x.ReleaseStock(orderId),
    JobContinuationOptions.OnlyOnDeletedState);
```

A job is deleted when someone deletes it, or when `AutomaticRetry` runs out of attempts and its `OnAttemptsExceeded` is `AttemptsExceededAction.Delete`. With the default (`Fail`) the parent stays `Failed` and this continuation keeps waiting. See [Retries](../filters/retries.md).

### Run whatever happens

```csharp
jobs.ContinueJobWith<OrderService>(
    chargeId,
    x => x.ReleaseStock(orderId),
    JobContinuationOptions.OnAnyFinishedState);
```

### On another queue

```csharp
jobs.ContinueJobWith<EmailService>(chargeId, "emails", x => x.SendReceipt(orderId));
```

### With a custom next state

```csharp
using Masterly.NightShift.States;

jobs.ContinueJobWith<OrderService>(
    chargeId,
    x => x.ShipAsync(orderId),
    new EnqueuedState("shipping"),
    JobContinuationOptions.OnlyOnSucceededState);
```

### Using the parent's result

```csharp
public class ImportService
{
    public Task<int> ImportAsync(string fileName)
    {
        // import the rows and return how many there were
        return Task.FromResult(42);
    }
}

public class ImportReport
{
    public void Send(string fileName, [FromResult] int rowsImported)
    {
        // e-mail "42 rows imported from customers.csv"
    }

    public void SendFailure(string fileName, [FromException] ExceptionInfo? error)
    {
        // e-mail error?.Message
    }
}

string? importId = jobs.Enqueue<ImportService>(x => x.ImportAsync(fileName));
if (importId != null)
{
    jobs.ContinueJobWith<ImportReport>(importId, x => x.Send(fileName, default));
    jobs.ContinueJobWith<ImportReport>(
        importId,
        x => x.SendFailure(fileName, null),
        JobContinuationOptions.OnlyOnDeletedState);
}
```

This needs `UseResultsInContinuations()`. For an async parent, the result is the value of its `Task<T>`.

## Things to know

- **The parent ID must not be `null`.** `Enqueue` and friends return `string?`, and `ContinueJobWith` needs a real ID. Check first, as in the examples. (Fan-in methods skip `null` parent IDs instead, see [Fan-in and batches](fan-in-and-batches.md).)
- **The parent must still exist.** A continuation for an expired or unknown parent fails with `BackgroundJobClientException`; its inner `InvalidOperationException` says "parent background job ... does not exist". Finished jobs expire after one day by default.
- **Failed parents block.** Continuations wait while the parent is `Failed`. Requeue or delete the parent to decide them.
- **A continuation can start just before its parent's final state is stored.** The children are moved to their next state while the parent's new state is chosen, before it is committed. A worker can pick a child up a moment before the parent shows `Succeeded`, so don't make the child read its parent's state and expect it final; pass what it needs with `[FromResult]` or as arguments. If that commit then fails, the children have started anyway: the parent's state change is retried, or the parent runs again, but its continuations don't start a second time, because they are no longer `Awaiting`. Deciding after the commit would avoid this, but a crash between the commit and the decision would leave the children waiting forever, which is worse. [Fan-in continuations](fan-in-and-batches.md) decide after the commit instead.
- **`NextState` is created now.** The state object you pass is stored when the child is created. `new ScheduledState(TimeSpan.FromMinutes(5))` computes its moment at that point, not when the parent finishes, so after a long parent the child is enqueued at once.
- **Results are serialized.** A return value that can't be serialized is stored as the text "Can not serialize the return value", and `[FromResult]` can't read it. Return small, serializable values; see [Job arguments](job-arguments.md#return-values).
- **Each continuation is a full job.** It has its own retries, filters and queue.

## See also

- [Fan-in and batches](fan-in-and-batches.md)
- [Fire-and-forget jobs](fire-and-forget.md)
- [Managing jobs](managing-jobs.md)
- [Retries](../filters/retries.md)
- [Global configuration](../configuration/global-configuration.md) (`UseResultsInContinuations`)
