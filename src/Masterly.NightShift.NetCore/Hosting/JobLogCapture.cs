using System.Diagnostics;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Masterly.NightShift;

/// <summary>Settings of the capture of log lines into the job console, see <c>AddNightShiftJobLogs</c>.</summary>
public sealed class NightShiftJobLogOptions
{
    /// <summary>The lowest level captured. <see cref="LogLevel.Information"/> by default.</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

    /// <summary>
    /// Categories left out, by prefix: Entity Framework's SQL commands and Masterly.NightShift's own lines by default.
    /// </summary>
    public ICollection<string> ExcludedCategories { get; } = ["Microsoft.EntityFrameworkCore.Database", "Masterly.NightShift"];

    /// <summary>The most lines a job's run captures, 1 to 10,000; one line at the end says how many were left out. 200 by default.</summary>
    public int MaxLinesPerJob
    {
        get;
        set => field = value is < 1 or > 10_000 ? throw new ArgumentOutOfRangeException(nameof(value), value, "Between 1 and 10,000.") : value;
    } = 200;

    /// <summary>The longest line kept, 100 to 65,536 characters; longer ones are cut. 2,048 by default.</summary>
    public int MaxMessageLength
    {
        get;
        set => field = value is < 100 or > 65_536 ? throw new ArgumentOutOfRangeException(nameof(value), value, "Between 100 and 65,536.") : value;
    } = 2048;

    /// <summary>
    /// Changes each line before it is stored, to hide secrets or personal data; a line it throws for is left out.
    /// <c>null</c>, the default, keeps lines as they are.
    /// </summary>
    public Func<string, string>? Redact { get; set; }

    /// <summary>Whether the console also gets a line when the run starts and one with its outcome. On by default.</summary>
    public bool LifecycleLines { get; set; } = true;
}

/// <summary>Captures the log lines a job writes into its console.</summary>
public static class NightShiftJobLogServiceCollectionExtensions
{
    /// <param name="services">The service collection.</param>
    extension([NotNull] IServiceCollection services)
    {
        /// <summary>
        /// Writes the <see cref="ILogger"/> lines logged while a job runs, from any category the options don't leave
        /// out, into the job's console, so they appear on its details page next to its progress. It applies to the
        /// servers added with <c>AddNightShiftServer</c> in this collection.
        /// </summary>
        /// <param name="configure">Changes the options, or <c>null</c> for the defaults.</param>
        /// <returns>The service collection.</returns>
        public IServiceCollection AddNightShiftJobLogs([CanBeNull] Action<NightShiftJobLogOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            NightShiftJobLogOptions options = new();
            configure?.Invoke(options);

            services.RemoveAll<NightShiftJobLogOptions>();
            services.AddSingleton(options);
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, JobConsoleLoggerProvider>());
            return services;
        }
    }
}

/// <summary>The run whose console receives log lines, flowing with the job's async code.</summary>
internal static class JobLogCapture
{
    private static readonly AsyncLocal<CaptureState?> CurrentState = new();

    public static CaptureState? Current => CurrentState.Value;

    /// <summary>Adds the capture filter to a server's filter provider when <c>AddNightShiftJobLogs</c> was called.</summary>
    [CanBeNull]
    public static IJobFilterProvider? AddTo([CanBeNull] IJobFilterProvider? filterProvider, [NotNull] IServiceProvider services)
    {
        if (ServerFilterProvider.Adds(filterProvider, static filter => filter is CaptureFilter)) return filterProvider;

        NightShiftJobLogOptions? options = services.GetService<NightShiftJobLogOptions>();
        if (options == null) return filterProvider;

        return new ServerFilterProvider(filterProvider ?? JobFilterProviders.Providers, new CaptureFilter(options));
    }

    internal sealed class CaptureFilter(NightShiftJobLogOptions options) : IServerFilter
    {
        private const string StartedItem = "Masterly.NightShift.JobLogs.Started";

        public NightShiftJobLogOptions Options { get; } = options;

        public void OnPerforming(PerformingContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            CaptureState state = new(context, Options);
            CurrentState.Value = state;
            context.Items[StartedItem] = Stopwatch.GetTimestamp();

            if (Options.LifecycleLines)
            {
                int attempt = context.GetJobParameter<int>("RetryCount", allowStale: true) + 1;
                state.WriteLifecycle(JobConsoleLevel.Info, attempt > 1 ? $"Started, attempt {attempt}" : "Started");
            }
        }

        public void OnPerformed(PerformedContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            CaptureState? state = CurrentState.Value;
            CurrentState.Value = null;
            if (state == null) return;

            if (Options.LifecycleLines)
            {
                string duration = context.Items.TryGetValue(StartedItem, out object? started) && started is long startedAt
                    ? $" in {Stopwatch.GetElapsedTime(startedAt).TotalSeconds:0.###} s"
                    : string.Empty;

                if (context.Exception != null && !context.ExceptionHandled)
                {
                    Exception error = context.Exception is JobPerformanceException { InnerException: { } inner } ? inner : context.Exception;
                    state.WriteLifecycle(JobConsoleLevel.Error, $"Failed{duration}: {error.GetType().Name}: {error.Message}");
                }
                else
                {
                    state.WriteLifecycle(JobConsoleLevel.Info, context.Canceled ? $"Canceled{duration}" : $"Finished{duration}");
                }
            }

            state.WriteDropped();
        }
    }
}

/// <summary>The lines of one run, counted against the limit.</summary>
internal sealed class CaptureState(PerformContext context, NightShiftJobLogOptions options)
{
    [ThreadStatic]
    private static bool _writing;

    private int _lines;
    private int _dropped;

    public NightShiftJobLogOptions Options { get; } = options;

    public void Write(JobConsoleLevel level, string category, string message)
    {
        // A line the console's own storage writes log doesn't come back here
        if (_writing) return;

        if (Interlocked.Increment(ref _lines) > Options.MaxLinesPerJob)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        string? text = Prepare($"[{ShortCategory(category)}] {message}");
        if (text != null) WriteToConsole(level, text);
    }

    public void WriteLifecycle(JobConsoleLevel level, string message) => WriteToConsole(level, $"[nightshift] {message}");

    public void WriteDropped()
    {
        int dropped = Volatile.Read(ref _dropped);
        if (dropped > 0) WriteToConsole(JobConsoleLevel.Warning, $"[nightshift] {dropped} more log lines were left out (MaxLinesPerJob is {Options.MaxLinesPerJob}).");
    }

    private string? Prepare(string text)
    {
        if (Options.Redact != null)
        {
            try
            {
                text = Options.Redact(text);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return null;
            }
        }

        return text.Length > Options.MaxMessageLength ? string.Concat(text.AsSpan(0, Options.MaxMessageLength - 1), "…") : text;
    }

    private void WriteToConsole(JobConsoleLevel level, string text)
    {
        _writing = true;
        try
        {
            context.Console.WriteLine(level, text);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The console keeps its own failures quiet; a line that can't be written is lost, the job carries on
        }
        finally
        {
            _writing = false;
        }
    }

    private static string ShortCategory(string category)
    {
        int dot = category.LastIndexOf('.');
        return dot >= 0 && dot < category.Length - 1 ? category[(dot + 1)..] : category;
    }
}

/// <summary>Writes the lines logged during a job into its console.</summary>
internal sealed class JobConsoleLoggerProvider(NightShiftJobLogOptions options) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new JobConsoleLogger(categoryName, options);

    public void Dispose()
    {
    }

    private sealed class JobConsoleLogger(string category, NightShiftJobLogOptions options) : ILogger
    {
        private readonly bool _excluded = options.ExcludedCategories.Any(x => category.StartsWith(x, StringComparison.Ordinal));

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel)
        {
            return !_excluded && logLevel != LogLevel.None && logLevel >= options.MinimumLevel && JobLogCapture.Current != null;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || JobLogCapture.Current is not { } capture) return;

            string message = formatter(state, exception);
            if (exception != null) message = $"{message} ({exception.GetType().Name}: {exception.Message})";

            capture.Write(ToConsoleLevel(logLevel), category, message);
        }

        private static JobConsoleLevel ToConsoleLevel(LogLevel level)
        {
            return level switch
            {
                LogLevel.Trace or LogLevel.Debug => JobConsoleLevel.Debug,
                LogLevel.Warning => JobConsoleLevel.Warning,
                LogLevel.Error or LogLevel.Critical => JobConsoleLevel.Error,
                _ => JobConsoleLevel.Info,
            };
        }
    }
}
