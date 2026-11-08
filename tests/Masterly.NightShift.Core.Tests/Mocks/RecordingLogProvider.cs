using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Masterly.NightShift.Logging;

namespace Masterly.NightShift.Core.Tests
{
    /// <summary>A log provider that keeps every message, to check what Masterly.NightShift logs.</summary>
    internal sealed class RecordingLogProvider : ILogProvider
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new ConcurrentQueue<LogEntry>();

        public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

        public ILog GetLogger(string name) => new RecordingLog(this, name);

        /// <summary>Makes this the provider of every Masterly.NightShift logger until the result is disposed.</summary>
        public IDisposable Use()
        {
            var previous = LateBoundLogProvider.Target;
            LateBoundLogProvider.Use(this);

            return new Restore(previous);
        }

        public sealed class LogEntry
        {
            public LogEntry(string logger, LogLevel level, string message, Exception exception)
            {
                Logger = logger;
                Level = level;
                Message = message;
                Exception = exception;
            }

            public string Logger { get; }
            public LogLevel Level { get; }
            public string Message { get; }
            public Exception Exception { get; }
        }

        private sealed class RecordingLog : ILog
        {
            private readonly RecordingLogProvider _provider;
            private readonly string _name;

            public RecordingLog(RecordingLogProvider provider, string name)
            {
                _provider = provider;
                _name = name;
            }

            public bool Log(LogLevel logLevel, Func<string> messageFunc, Exception exception = null)
            {
                if (messageFunc == null) return true;

                _provider._entries.Enqueue(new LogEntry(_name, logLevel, messageFunc(), exception));
                return true;
            }
        }

        private sealed class Restore : IDisposable
        {
            private readonly ILogProvider _previous;

            public Restore(ILogProvider previous)
            {
                _previous = previous;
            }

            public void Dispose()
            {
                LateBoundLogProvider.Target = _previous;
                LogProvider.SetCurrentLogProvider(LateBoundLogProvider.Instance);
            }
        }
    }
}
