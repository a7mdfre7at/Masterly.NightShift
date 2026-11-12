using System.Collections.Concurrent;
using Masterly.NightShift.Logging;

namespace Masterly.NightShift.Core.Tests;

/// <summary>A log provider that keeps every message, to check what Masterly.NightShift logs.</summary>
internal sealed class RecordingLogProvider : ILogProvider
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyCollection<LogEntry> Entries => [.. _entries];

    public ILog GetLogger(string name) => new RecordingLog(this, name);

    /// <summary>Makes this the provider of every Masterly.NightShift logger until the result is disposed.</summary>
    public IDisposable Use()
    {
        ILogProvider? previous = LateBoundLogProvider.Target;
        LateBoundLogProvider.Use(this);

        return new Restore(previous);
    }

    public sealed class LogEntry(string logger, LogLevel level, string message, Exception exception)
    {
        public string Logger { get; } = logger;
        public LogLevel Level { get; } = level;
        public string Message { get; } = message;
        public Exception Exception { get; } = exception;
    }

    private sealed class RecordingLog(RecordingLogProvider provider, string name) : ILog
    {
        private readonly RecordingLogProvider _provider = provider;
        private readonly string _name = name;

        public bool Log(LogLevel logLevel, Func<string> messageFunc, Exception exception = null)
        {
            if (messageFunc == null)
            {
                return true;
            }

            _provider._entries.Enqueue(new LogEntry(_name, logLevel, messageFunc(), exception));
            return true;
        }
    }

    private sealed class Restore(ILogProvider previous) : IDisposable
    {
        private readonly ILogProvider _previous = previous;

        public void Dispose()
        {
            LateBoundLogProvider.Target = _previous;
            LogProvider.SetCurrentLogProvider(LateBoundLogProvider.Instance);
        }
    }
}
