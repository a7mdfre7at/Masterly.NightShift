# Source-generated invokers

When a server runs a job, it has to call a method it only knows by name. Masterly.NightShift does this without reflection where it can: a source generator, shipped inside the `Masterly.NightShift.Core` package, writes a direct, typed call for each job method at build time. You usually don't have to do anything. This page explains what is generated, when you need `[GenerateJobInvokers]`, and how to check or turn it off.

## Quick example

Nothing to configure for jobs enqueued in the same project:

```csharp
using Masterly.NightShift;

jobs.Enqueue<ReportJobs>(x => x.BuildAsync(42));   // the generator sees this call and emits an invoker for BuildAsync
```

For a class whose jobs are enqueued from another project, or created only at run time (recurring job manifests, the dashboard's Catalog), mark the class:

```csharp
[GenerateJobInvokers]
public class ReportJobs
{
    public Task BuildAsync(int reportId) => Task.CompletedTask;
    public void Archive(int reportId) { }
}
```

Every public method of `ReportJobs` now gets an invoker, in the assembly that declares the class.

## How it works

Every job method is performed by a `JobInvoker`, resolved once per method and cached by `JobInvokerRegistry`. There are three kinds (`JobInvokerKind`), tried in this order:

| Kind | When it is used | How the call is made |
|---|---|---|
| `Generated` | An assembly loaded in the process registered a generated invoker for the method | A direct, typed call compiled into that assembly |
| `Compiled` | No generated invoker, `UseCompiledJobInvokers` is on (the default), and the runtime can compile expression trees | A delegate compiled from an expression tree, once per method |
| `Reflection` | Neither of the above (for example `UseCompiledJobInvokers(false)`, or a runtime without dynamic code such as Native AOT) | `MethodInfo.Invoke` |

The results are the same with every kind; only the speed of the call differs. Arguments are deserialized to the parameter types before the call, so they normally match. Only an argument that an [interceptor](../filters/custom-filters.md) replaced with a value of another type is handled differently. Every kind passes `null` for a non-nullable value type as its default value, and accepts a boxed value of an enum's underlying type for that enum (`2` for `Color.Green`). For any other type, the generated and compiled invokers throw `InvalidCastException` (a `long` for an `int` parameter, and also an `int` for a `long` one). The reflection invoker converts like `MethodInfo.Invoke`: it widens numbers (an `int` for a `long` parameter works) and throws `ArgumentException` otherwise (a `long` for an `int` parameter).

The generator (`JobInvokerGenerator`) runs in every C# project that references a Masterly.NightShift package. It finds job methods in two places:

1. **Lambdas passed to Masterly.NightShift methods.** An expression lambda whose body is a method call, passed directly as an argument to a method in a `Masterly.NightShift` namespace: `Enqueue`, `Schedule`, `ContinueJobWith`, `AddOrUpdate`, `Job.FromExpression` and the rest.
2. **Types marked `[GenerateJobInvokers]`.** Every public, non-generic method declared on the class, record, interface or struct (inherited methods are not included), except overrides of `Equals`, `GetHashCode` and `ToString`.

It skips methods it can't call safely from generated code, and those jobs use a compiled invoker at run time instead: non-public methods, methods of open generic types or of types not accessible from the assembly, methods with `ref`/`out`/`in` parameters or `ref` returns, methods that use pointers, `ref struct`s, `dynamic` or anonymous types, `async void` methods, `static abstract` and `static virtual` interface members, and members the compiler generates (a record's `Equals`, `GetHashCode`, `ToString` and `<Clone>$`). A method whose name is a C# keyword, such as `@event`, is called as `@event` and works.

The generated code is one internal class per assembly, `Masterly.NightShift.Generated.NightShiftJobInvokers_{AssemblyName}`, in the file `NightShiftJobInvokers.g.cs`. It registers its invokers through a module initializer when the project's C# version (9 or later) and target framework support one. Otherwise (for example a `netstandard2.0` library on an older language version) the assembly is marked with `JobInvokersProviderAttribute`, and the registry finds it the first time an invoker is needed, and again whenever a new assembly is loaded.

The dashboard's Catalog page is built from the registered generated invokers (see [Job catalog](job-catalog.md)), and the Insights page shows how many methods each kind of invoker served.

## API

### Attribute and build switch

| Name | Kind | Meaning |
|---|---|---|
| `[GenerateJobInvokers]` | Attribute on a class, record, interface or struct (not inherited by derived types) | Generates an invoker for every public, non-generic method declared on the type, except `object` overrides and compiler-generated members |
| `NightShiftDisableJobInvokerGeneration` | MSBuild property, `true` to turn off | Skips generation for the project; its jobs use compiled invokers |
| `NSGEN001` | Build warning | The generator hit an unexpected error; the affected jobs use compiled invokers. The build doesn't fail |

### Configuration

| Method | Default | Meaning |
|---|---|---|
| `UseCompiledJobInvokers(bool enabled)` on `IGlobalConfiguration` | `true` | `false` makes every method without a generated invoker use reflection. Methods already resolved are not affected |

### JobInvokerRegistry

In the `Masterly.NightShift.Common` namespace.

| Member | Meaning |
|---|---|
| `UseCompiledInvokers` (`bool`, default `true`) | The switch `UseCompiledJobInvokers` sets |
| `GetStatistics()` | A `JobInvokerStatistics`: `Registered` (generated invokers registered), `Generated`, `Compiled` and `Reflection` (methods performed with each kind so far) |
| `GetInvoker(MethodInfo method)` | The invoker for a method; never `null` |
| `TryGetGeneratedInvoker(MethodInfo method, out JobInvoker? invoker)` | Whether a generated invoker is registered for the method |
| `GetGeneratedInvokers()` | Every registered generated invoker, sorted by type and method |
| `Register(JobInvoker invoker)` | Called by generated code. A later registration for the same method replaces the earlier one |
| `RegisterAssembly(Assembly assembly)` | Registers the invokers an assembly provides through `JobInvokersProviderAttribute`; safe to call twice |
| `ClearResolved()` | Forgets the cached invokers so they are resolved again. For tests |

## More examples

### Turning generation off for one project

```xml
<PropertyGroup>
  <NightShiftDisableJobInvokerGeneration>true</NightShiftDisableJobInvokerGeneration>
</PropertyGroup>
```

### Using reflection only

```csharp
services.AddNightShift(config => config
    .UseCompiledJobInvokers(false)
    .UseSqlServerStorage(connectionString));
```

Only useful to rule out invokers while you investigate a problem.

### Checking what the server uses

```csharp
using Masterly.NightShift.Common;

JobInvokerStatistics stats = JobInvokerRegistry.GetStatistics();
Console.WriteLine($"generated {stats.Generated}, compiled {stats.Compiled}, reflection {stats.Reflection}, registered {stats.Registered}");

bool hasGenerated = JobInvokerRegistry.TryGetGeneratedInvoker(typeof(ReportJobs).GetMethod(nameof(ReportJobs.Archive))!, out JobInvoker? invoker);
```

### Seeing the generated code

Build with `-p:EmitCompilerGeneratedFiles=true` and look for `NightShiftJobInvokers.g.cs` under `obj/<configuration>/<target framework>/generated`.

## Things to know

- **The invoker lives where the lambda is, unless you mark the type.** A generated invoker is compiled into the project that contains the `Enqueue` call. If the server is a separate process that doesn't load that project (a worker service that only processes jobs created by a web app), it has no generated invoker for the method and uses a compiled one. Put `[GenerateJobInvokers]` on job classes in a shared library, so the invokers travel with the classes.
- **Only direct lambdas are found.** `Expression<Func<ReportJobs, Task>> call = x => x.BuildAsync(42); jobs.Enqueue(call);` is not seen by the generator, because the lambda isn't an argument of a Masterly.NightShift method. The job still works, with a compiled invoker.
- **The generator needs a direct package reference.** NuGet doesn't pass analyzers through a project reference by default, so a project that gets Masterly.NightShift only through another project of yours doesn't run the generator. Reference a Masterly.NightShift package from that project, or mark the job types with `[GenerateJobInvokers]` in a project that does.
- **A missing generated invoker is never an error.** The fallbacks give the same result, so generation is purely an optimization.
- **Jobs on a captured instance** (`() => service.Run()`) are stored with the instance's runtime type. If that differs from the type the lambda names (an interface variable holding a class), the generated invoker doesn't match and a compiled one is used.

## See also

- [Job arguments](job-arguments.md)
- [Job catalog](job-catalog.md)
- [Insights](../monitoring/insights.md)
- [Global configuration](../configuration/global-configuration.md)
- [Architecture for contributors](../contributing/architecture.md)
