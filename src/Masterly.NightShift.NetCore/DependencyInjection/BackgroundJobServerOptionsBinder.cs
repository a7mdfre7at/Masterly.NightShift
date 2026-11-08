#nullable enable

using System;
using System.Globalization;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace Masterly.NightShift
{
    /// <summary>
    /// Reads the plain settings of <see cref="BackgroundJobServerOptions"/> from a configuration section, without
    /// a dependency on the configuration binder package. A setting missing from the section keeps its value.
    /// </summary>
    internal static class BackgroundJobServerOptionsBinder
    {
        public static void Bind(IConfiguration configuration, BackgroundJobServerOptions options)
        {
            Read(configuration, nameof(options.ServerName), static text => text, value => options.ServerName = value);
            Read(configuration, nameof(options.WorkerCount), ParseInt32, value => options.WorkerCount = value);
            Read(configuration, nameof(options.IsLightweightServer), Boolean.Parse, value => options.IsLightweightServer = value);
            Read(configuration, nameof(options.StopTimeout), ParseTimeSpan, value => options.StopTimeout = value);
            Read(configuration, nameof(options.ShutdownTimeout), ParseTimeSpan, value => options.ShutdownTimeout = value);
            Read(configuration, nameof(options.SchedulePollingInterval), ParseTimeSpan, value => options.SchedulePollingInterval = value);
            Read(configuration, nameof(options.HeartbeatInterval), ParseTimeSpan, value => options.HeartbeatInterval = value);
            Read(configuration, nameof(options.ServerCheckInterval), ParseTimeSpan, value => options.ServerCheckInterval = value);
            Read(configuration, nameof(options.ServerTimeout), ParseTimeSpan, value => options.ServerTimeout = value);
            Read(configuration, nameof(options.CancellationCheckInterval), ParseTimeSpan, value => options.CancellationCheckInterval = value);
            Read(configuration, nameof(options.MaxDegreeOfParallelismForSchedulers), ParseInt32, value => options.MaxDegreeOfParallelismForSchedulers = value);
            ReadQueues(configuration, options);
        }

        private static void Read<T>(IConfiguration configuration, string key, Func<string, T> parse, Action<T> assign)
        {
            var section = configuration.GetSection(key);
            if (String.IsNullOrWhiteSpace(section.Value)) return;

            try
            {
                assign(parse(section.Value!.Trim()));
            }
            catch (Exception ex) when (ex is FormatException || ex is OverflowException || ex is ArgumentException)
            {
                throw new InvalidOperationException($"The configuration value '{section.Path}' ('{section.Value}') is not a valid {key}: {ex.Message}", ex);
            }
        }

        // An array ("Queues": ["critical", "default"]) or a comma-separated value ("critical,default"), which is easier
        // to set from an environment variable. The list replaces the default queue rather than being added to it.
        // Configuration merges both forms from different sources (an array in appsettings.json, a value in an
        // environment variable) without telling which source came last, so the value wins: only a scalar source sets
        // it, and that is the form used to override an array.
        private static void ReadQueues(IConfiguration configuration, BackgroundJobServerOptions options)
        {
            var section = configuration.GetSection(nameof(options.Queues));
            string?[] values = !String.IsNullOrWhiteSpace(section.Value)
                ? section.Value!.Split(',')
                : section.GetChildren().Select(static child => child.Value).ToArray();

            var queues = values.Where(static value => !String.IsNullOrWhiteSpace(value)).Select(static value => value!.Trim()).ToArray();
            if (queues.Length == 0) return;

            options.Queues = queues;
        }

        private static int ParseInt32(string text)
        {
            return Int32.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }

        private static TimeSpan ParseTimeSpan(string text)
        {
            return TimeSpan.Parse(text, CultureInfo.InvariantCulture);
        }
    }
}
