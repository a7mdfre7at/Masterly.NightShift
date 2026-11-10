using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using Masterly.NightShift.Annotations;

// ReSharper disable LocalizableElement
// ReSharper disable AssignNullToNotNullAttribute


namespace Masterly.NightShift.Core.Tests.Common;

public class JobFacts
{
    private static readonly DateTime SomeDateTime = new(2014, 5, 30, 12, 0, 0);

    private readonly Type _type;
    private readonly MethodInfo _method;
    private readonly object[] _arguments;
    private readonly string _queue;

    public JobFacts()
    {
        _type = typeof(JobFacts);
        _method = _type.GetMethod("StaticMethod");
        _arguments = [];
        _queue = "critical";
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenTheTypeIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            // ReSharper disable once AssignNullToNotNullAttribute
            () => new Job(null, _method, _arguments));

        Assert.Equal("type", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenTheMethodIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            // ReSharper disable once AssignNullToNotNullAttribute
            () => new Job(_type, null, _arguments));

        Assert.Equal("method", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenTheTypeDoesNotContainTheGivenMethod()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new Job(typeof(Job), _method, _arguments));

        Assert.Equal("type", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenArgumentsArrayIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            // ReSharper disable once AssignNullToNotNullAttribute
            () => new Job(_type, _method, (object[])null));

        Assert.Equal("args", exception.ParamName);
    }

    [Fact]
    public void Ctor_DoesNotThrow_WhenQueueIsNull()
    {
        Job job = new(_type, _method, _arguments, null);
        Assert.NotNull(job);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenQueueValidationFails()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new Job(_type, _method, _arguments, "&^*%"));

        Assert.Equal("queue", exception.ParamName);
    }

    [Fact]
    public void Ctor_InitializesAllProperties()
    {
        Job job = new(_type, _method, _arguments);

        Assert.Same(_type, job.Type);
        Assert.Same(_method, job.Method);
        Assert.True(_arguments.SequenceEqual(InvocationData.SerializeArguments(job.Method, job.Args)));
        Assert.Null(job.Queue);
    }

    [Fact]
    public void Ctor_WithQueue_InitializesAllTheProperties()
    {
        Job job = new(_type, _method, _arguments, _queue);

        Assert.Same(_type, job.Type);
        Assert.Same(_method, job.Method);
        Assert.True(_arguments.SequenceEqual(job.Args));
        Assert.Equal(_queue, job.Queue);
    }

    [Fact]
    public void Ctor_HasDefaultValueForArguments()
    {
        Job job = new(_type, _method);

        Assert.Empty(InvocationData.SerializeArguments(job.Method, job.Args));
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenArgumentCountIsNotEqualToParameterCount()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new Job(_type, _method, ["hello!"]));

        Assert.Contains("count", exception.Message);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenMethodContains_UnassignedGenericTypeParameters()
    {
        MethodInfo? method = _type.GetMethod("GenericMethod");

        Assert.Throws<NotSupportedException>(
            () => new Job(_type, method, ["hello!"]));
    }

    [Fact]
    public void Ctor_CanUsePropertyValues_OfAnotherJob_AsItsArguments()
    {
        MethodInfo? method = _type.GetMethod("MethodWithArguments");
        Job job = new(_type, method, "hello", 456);

        Job anotherJob = new(job.Type, job.Method, job.Args);

        Assert.Equal(_type, anotherJob.Type);
        Assert.Equal(method, anotherJob.Method);
        Assert.Equal("hello", anotherJob.Args[0]);
        Assert.Equal(456, anotherJob.Args[1]);
    }

    [Fact]
    public void FromExpression_Action_ThrowsException_WhenNullExpressionProvided()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => Job.FromExpression((Expression<Action>)null));

        Assert.Equal("methodCall", exception.ParamName);
    }

    [Fact]
    public void FromExpression_Action_DoesNotThrowAnException_WhenNullQueueProvided()
    {
        Job job = Job.FromExpression(() => Console.WriteLine(), null);
        Assert.NotNull(job);
    }

    [Fact]
    public void FromExpression_ThrowsAnException_WhenNewExpressionIsGiven()
    {
        Assert.Throws<ArgumentException>(
            // ReSharper disable once ObjectCreationAsStatement
            () => Job.FromExpression(() => new JobFacts()));
    }

    [Fact]
    public void FromExpression_Action_ReturnsTheJob()
    {
        Job job = Job.FromExpression(() => Console.WriteLine());

        Assert.Equal(typeof(Console), job.Type);
        Assert.Equal("WriteLine", job.Method.Name);
        Assert.Null(job.Queue);
    }

    [Fact]
    public void FromExpression_ActionWithQueue_ReturnsTheJobWithQueueSet()
    {
        Job job = Job.FromExpression(() => Console.WriteLine(), "critical");

        Assert.Equal(typeof(Console), job.Type);
        Assert.Equal("WriteLine", job.Method.Name);
        Assert.Equal("critical", job.Queue);
    }

    [Fact]
    public void FromExpression_Func_ThrowsException_WhenNullExpressionProvided()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => Job.FromExpression((Expression<Func<Task>>)null));

        Assert.Equal("methodCall", exception.ParamName);
    }

    [Fact]
    public void FromExpression_Func_DoesNotThrowAnException_WhenNullQueueProvided()
    {
        Job job = Job.FromExpression(() => AsyncMethod(), null);
        Assert.NotNull(job);
    }

    [Fact]
    public void FromExpression_Func_ReturnsTheJob()
    {
        Job job = Job.FromExpression(() => AsyncMethod());

        Assert.Equal(typeof(JobFacts), job.Type);
        Assert.Equal("AsyncMethod", job.Method.Name);
        Assert.Null(job.Queue);
    }

    [Fact]
    public void FromExpression_FuncWithQueue_ReturnsTheJobWithQueueSet()
    {
        Job job = Job.FromExpression(() => AsyncMethod(), "critical");

        Assert.Equal(typeof(JobFacts), job.Type);
        Assert.Equal("AsyncMethod", job.Method.Name);
        Assert.Equal("critical", job.Queue);
    }

    [Fact]
    public void FromExpression_ConvertsDateTimeRepresentation_ToIso8601Format()
    {
        DateTime date = new(2014, 5, 30, 12, 0, 0, 777);
        string expected = date.ToString("o");

        Job job = Job.FromExpression(() => MethodWithDateTimeArgument(date));

        Assert.Equal(expected, InvocationData.SerializeArguments(job.Method, job.Args)[0]);
    }

    [Fact]
    public void FromExpression_ConvertsArgumentsToJson()
    {
        Job job = Job.FromExpression(() => MethodWithArguments("123", 1));

        Assert.Equal("\"123\"", InvocationData.SerializeArguments(job.Method, job.Args)[0]);
        Assert.Equal("1", InvocationData.SerializeArguments(job.Method, job.Args)[1]);
    }

    [Fact]
    public void FromExpression_ConvertsObjectArgumentsToJson()
    {
        Job job = Job.FromExpression(() => MethodWithObjectArgument("hello"));

        Assert.Equal("\"hello\"", InvocationData.SerializeArguments(job.Method, job.Args)[0]);
    }

    [Fact]
    public void FromExpression_ReturnValueDoesNotDepend_OnCurrentCulture()
    {
        DateTime date = DateTime.UtcNow;

        CultureHelper.SetCurrentCulture("en-US");
        Job enJob = Job.FromExpression(() => MethodWithDateTimeArgument(date));

        CultureHelper.SetCurrentCulture("ru-RU");
        Job ruJob = Job.FromExpression(() => MethodWithDateTimeArgument(date));

        Assert.Equal(InvocationData.SerializeArguments(enJob.Method, enJob.Args)[0], InvocationData.SerializeArguments(ruJob.Method, ruJob.Args)[0]);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenMethodIsAsyncVoid()
    {
        MethodInfo? method = typeof(JobFacts).GetMethod(nameof(AsyncVoidMethod));

        Assert.Throws<NotSupportedException>(
            () => new Job(typeof(JobFacts), method, []));
    }

    [Fact]
    public void FromInstanceExpression_Action_ThrowsException_WhenNullExpressionIsProvided()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => Job.FromExpression((Expression<Action<JobFacts>>)null));

        Assert.Equal("methodCall", exception.ParamName);
    }

    [Fact]
    public void FromInstanceExpression_Action_DoesNotThrowAnException_WhenNullQueueIsProvided()
    {
        Job job = Job.FromExpression<Instance>(x => x.Method(), null);
        Assert.NotNull(job);
    }

    [Fact]
    public void FromInstanceExpression_Func_ThrowsException_WhenNullExpressionIsProvided()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => Job.FromExpression((Expression<Func<JobFacts, Task>>)null));

        Assert.Equal("methodCall", exception.ParamName);
    }

    [Fact]
    public void FromInstanceExpression_Func_DoesNotThrowAnException_WhenNullQueueIsProvided()
    {
        Job job = Job.FromExpression<Instance>(x => x.FunctionReturningTask(), null);
        Assert.NotNull(job);
    }

    [Fact]
    public void FromInstanceExpression_ThrowsAnException_WhenNewExpressionIsGiven()
    {
        Assert.Throws<ArgumentException>(
            // ReSharper disable once ObjectCreationAsStatement
            () => Job.FromExpression<JobFacts>(x => new JobFacts()));
    }

    [Fact]
    public void FromInstanceExpression_Action_ReturnsCorrectResult()
    {
        Job job = Job.FromExpression<Instance>(x => x.Method());

        Assert.Equal(typeof(Instance), job.Type);
        Assert.Equal("Method", job.Method.Name);
        Assert.Null(job.Queue);
    }

    [Fact]
    public void FromInstanceExpression_ActionWithQueue_ReturnsCorrectResultWithQueueSet()
    {
        Job job = Job.FromExpression<Instance>(x => x.Method(), "critical");

        Assert.Equal(typeof(Instance), job.Type);
        Assert.Equal("Method", job.Method.Name);
        Assert.Equal("critical", job.Queue);
    }

    [Fact]
    public void FromInstanceExpression_Func_ReturnsCorrectResult()
    {
        Job job = Job.FromExpression<Instance>(x => x.FunctionReturningTask());

        Assert.Equal(typeof(Instance), job.Type);
        Assert.Equal("FunctionReturningTask", job.Method.Name);
        Assert.Null(job.Queue);
    }

    [Fact]
    public void FromInstanceExpression_FuncWithQueue_ReturnsCorrectResultWithQueueSet()
    {
        Job job = Job.FromExpression<Instance>(x => x.FunctionReturningTask(), "critical");

        Assert.Equal(typeof(Instance), job.Type);
        Assert.Equal("FunctionReturningTask", job.Method.Name);
        Assert.Equal("critical", job.Queue);
    }

    [Fact]
    public void FromNonGenericExpression_InfersType_FromAGivenObject()
    {
        IDisposable instance = new Instance();
        Job job = Job.FromExpression(() => instance.Dispose());

        Assert.Equal(typeof(Instance), job.Type);
    }

    [Fact]
    public void FromNonGenericExpression_InfersACorrectMethod_FromAGivenObject_WhenInterfaceTreeIsUsed()
    {
        IDisposable instance = new Instance();
        Job job = Job.FromExpression(() => instance.Dispose());

        Assert.Equal(typeof(Instance), job.Method.DeclaringType);
    }

    [Fact]
    public void FromNonGenericExpression_ThrowsAnException_IfGivenObjectIsNull()
    {
        IDisposable instance = null;

        Assert.Throws<InvalidOperationException>(
            () => Job.FromExpression(() => instance.Dispose()));
    }

    [Fact]
    public void FromGenericExpression_InfersType_FromAGivenObject_AndHandlesAssignableParameters()
    {
        IServiceInterface<MyDerivedClass> service = new MyBaseClassService();
        MyDerivedClass myClass = new();

        Job job = Job.FromExpression(() => service.MyMethod(myClass));

        Assert.Equal(typeof(MyBaseClassService), job.Type);
        Assert.Equal("MyMethod", job.Method.Name);
        Assert.Equal(typeof(MyBaseClassService), job.Method.DeclaringType);
    }

    [Fact]
    public void FromScopedExpression_HandlesGenericMethods()
    {
        CommandDispatcher dispatcher = new();
        Job job = Job.FromExpression(() => dispatcher.DispatchTyped(123));

        Assert.Equal(typeof(CommandDispatcher), job.Type);
        Assert.Equal(typeof(CommandDispatcher), job.Method.DeclaringType);
    }

    [Fact]
    public void FromScopedExpression_HandlesMethodsDeclaredInBaseClasse()
    {
        DerivedInstance instance = new();
        Job job = Job.FromExpression(() => instance.Method());

        Assert.Equal(typeof(DerivedInstance), job.Type);
        Assert.Equal(typeof(Instance), job.Method.DeclaringType);
    }

    [Fact]
    public void FromScopedExpression_ThrowsWhenExplicitInterfaceImplementationIsPassed()
    {
        IService service = new ServiceImpl();
        Assert.Throws<NotSupportedException>(() => Job.FromExpression(() => service.Method()));
    }

    public interface IService
    {
        void Method();
    }

    public class ServiceImpl : IService
    {
        void IService.Method()
        {
        }
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenMethodContainsReferenceParameter()
    {
        string test = null;
        Assert.Throws<NotSupportedException>(
            () => Job.FromExpression(() => MethodWithReferenceParameter(ref test)));
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenMethodContainsOutputParameter()
    {
        string test;
        Assert.Throws<NotSupportedException>(
            () => Job.FromExpression(() => MethodWithOutputParameter(out test)));
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenMethodIsNotPublic()
    {
        Assert.Throws<NotSupportedException>(
            () => Job.FromExpression(() => PrivateMethod()));
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenMethodParametersContainADelegate()
    {
        Assert.Throws<NotSupportedException>(
            () => Job.FromExpression(() => DelegateMethod(() => Console.WriteLine("Hey delegate!"))));
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenMethodParametersContainAnExpression()
    {
        Assert.Throws<NotSupportedException>(
            () => Job.FromExpression(() => ExpressionMethod(() => Console.WriteLine("Hey expression!"))));
    }

    [Fact]
    public void DeserializeArguments_ThrowsJsonException_WhenAStoredArgumentCanNotBeRead()
    {
        MethodInfo? method = typeof(JobFacts).GetMethod("MethodWithDateTimeArgument");

        Assert.ThrowsAny<JsonException>(
            () => InvocationData.DeserializeArguments(method, [TestJson.ToJson("sdfa")]));
    }

    [Fact]
    public void GetTypeFilterAttributes_ReturnsCorrectAttributes()
    {
        Job job = Job.FromExpression<Instance>(x => x.Method());
        JobFilterAttribute[] nonCachedAttributes = [.. job.GetTypeFilterAttributes(false)];
        JobFilterAttribute[] cachedAttributes = [.. job.GetTypeFilterAttributes(true)];

        Assert.Single(nonCachedAttributes);
        Assert.Single(cachedAttributes);

        Assert.True(nonCachedAttributes[0] is TestTypeAttribute);
        Assert.True(cachedAttributes[0] is TestTypeAttribute);
    }

    [Fact]
    public void GetMethodFilterAttributes_ReturnsCorrectAttributes()
    {
        Job job = Job.FromExpression<Instance>(x => x.Method());
        JobFilterAttribute[] nonCachedAttributes = [.. job.GetMethodFilterAttributes(false)];
        JobFilterAttribute[] cachedAttributes = [.. job.GetMethodFilterAttributes(true)];

        Assert.Single(nonCachedAttributes);
        Assert.Single(cachedAttributes);

        Assert.True(nonCachedAttributes[0] is TestMethodAttribute);
        Assert.True(cachedAttributes[0] is TestMethodAttribute);
    }

    private static void PrivateMethod()
    {
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public static void MethodWithReferenceParameter(ref string a)
    {
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public static void MethodWithOutputParameter(out string a)
    {
        a = "hello";
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public static void StaticMethod()
    {
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    [SuppressMessage("ReSharper", "MemberCanBeMadeStatic.Global")]
    public void InstanceMethod()
    {
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    [SuppressMessage("ReSharper", "MemberCanBeMadeStatic.Global")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public void MethodWithArguments(string stringArg, int intArg)
    {

        Assert.Equal("hello", stringArg);
        Assert.Equal(5, intArg);
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    [SuppressMessage("ReSharper", "MemberCanBeMadeStatic.Global")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public void MethodWithObjectArgument(object argument)
    {

        Assert.Equal("5", argument);
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    public void MethodWithCustomArgument(Instance argument)
    {
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    [SuppressMessage("ReSharper", "MemberCanBeMadeStatic.Global")]
    public void MethodWithDateTimeArgument(DateTime argument)
    {

        Assert.Equal(SomeDateTime, argument);
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public static void ExceptionMethod()
    {
        throw new InvalidOperationException("exception");
    }

    [UsedImplicitly]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public void GenericMethod<T>(T arg)
    {
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    [SuppressMessage("ReSharper", "MemberCanBeMadeStatic.Global")]
    public Task AsyncMethod()
    {
        TaskCompletionSource<bool> source = new();
        return source.Task;
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public async void AsyncVoidMethod()
    {
        await Task.Yield();
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBeMadeStatic.Global")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public void DelegateMethod(Action action)
    {
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    [SuppressMessage("ReSharper", "MemberCanBeMadeStatic.Global")]
    public void ExpressionMethod(Expression<Action> expression)
    {
    }

    private interface ICommandDispatcher
    {
        void DispatchTyped<TCommand>(TCommand command);
    }

    private sealed class CommandDispatcher : ICommandDispatcher
    {
        public void DispatchTyped<TCommand>(TCommand command)
        {
        }
    }

    [TestType]
    public class Instance : IDisposable
    {
        [TestMethod]
        public void Method()
        {
        }

        public void Dispose()
        {
        }

        public string FunctionReturningValue()
        {
            return "Return value";
        }

        public async Task FunctionReturningTask()
        {
            await Task.Yield();
        }

        public async Task FunctionReturningValueTask()
        {
            await Task.Yield();
        }

        public async Task<string> FunctionReturningTaskResultingInString(bool continueOnCapturedContext)
        {
            await Task.Yield();
            await Task.Delay(15).ConfigureAwait(continueOnCapturedContext);

            return FunctionReturningValue();
        }

        public ValueTask<string> FunctionReturningValueTaskResultingInString(bool continueOnCapturedContext)
        {
            return new ValueTask<string>(FunctionReturningTaskResultingInString(continueOnCapturedContext));
        }
    }

    public class DerivedInstance : Instance
    {
    }

    // ReSharper disable once UnusedTypeParameter
    public class JobClassWrapper<T> : IDisposable where T : IDisposable
    {
        public void Dispose()
        {
        }
    }

    public class TestTypeAttribute : JobFilterAttribute
    {
    }

    public class TestMethodAttribute : JobFilterAttribute
    {
    }

    private class MyBaseClass
    {
        public string BaseProp { get; set; }
    }

    private sealed class MyDerivedClass : MyBaseClass
    {
        public int MyProperty { get; set; }
    }

    private interface IServiceInterface<in T> where T : MyBaseClass
    {
        Task MyMethod(T input);
    }

    private sealed class MyBaseClassService : IServiceInterface<MyBaseClass>
    {
        public Task MyMethod(MyBaseClass input)
        {
            return Task.FromResult(true);
        }
    }
}
