using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Masterly.NightShift;

/// <summary>
/// Which alerts a sink receives: their kinds, the jobs they are about, and how often the same alert may repeat.
/// <code>
/// config.UseAlerts(sink, rule => rule
///     .ForKinds(AlertKind.JobFailed | AlertKind.MonitorIncident)
///     .ForMethods("Billing*.*", "ReportService.BuildDaily")
///     .ExceptMethods("*.Ping")
///     .WithCooldown(TimeSpan.FromMinutes(15)));
/// </code>
/// </summary>
public sealed class AlertRule
{
    private readonly List<Regex> _include = [];
    private readonly List<Regex> _exclude = [];
    private readonly ConcurrentDictionary<string, DateTime> _lastSent = new(StringComparer.Ordinal);
    private long _suppressed;

    /// <summary>The kinds of alerts let through. Every kind by default.</summary>
    public AlertKind Kinds { get; private set; } = AlertKind.All;

    /// <summary>
    /// How long the same alert (kind, monitor, job method and resource) is held back after it was let through. Zero, the
    /// default, lets every alert through.
    /// </summary>
    public TimeSpan Cooldown { get; private set; }

    /// <summary>The number of alerts held back by the cooldown.</summary>
    public long Suppressed => Interlocked.Read(ref _suppressed);

    /// <summary>Lets only these kinds through.</summary>
    /// <returns>The same rule, for chaining.</returns>
    public AlertRule ForKinds(AlertKind kinds)
    {
        Kinds = kinds;
        return this;
    }

    /// <summary>
    /// Lets through only alerts about jobs whose method, as <c>Type.Method</c>, matches one of the patterns. <c>*</c>
    /// matches any characters and <c>?</c> one; case matters. Alerts about no job (a circuit) aren't affected.
    /// </summary>
    /// <returns>The same rule, for chaining.</returns>
    public AlertRule ForMethods([NotNull] params string[] patterns)
    {
        AddPatterns(_include, patterns);
        return this;
    }

    /// <summary>Holds back alerts about jobs whose method matches one of the patterns, as <see cref="ForMethods"/> reads them.</summary>
    /// <returns>The same rule, for chaining.</returns>
    public AlertRule ExceptMethods([NotNull] params string[] patterns)
    {
        AddPatterns(_exclude, patterns);
        return this;
    }

    /// <summary>Sets <see cref="Cooldown"/>.</summary>
    /// <returns>The same rule, for chaining.</returns>
    public AlertRule WithCooldown(TimeSpan cooldown)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cooldown, TimeSpan.Zero);
        Cooldown = cooldown;
        return this;
    }

    /// <summary>Whether an alert passes the rule; one let through starts its cooldown.</summary>
    public bool Allows([NotNull] Alert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        if ((Kinds & alert.Kind) == 0)
        {
            return false;
        }

        if (alert.Method != null)
        {
            if (_include.Count > 0 && !_include.Any(x => x.IsMatch(alert.Method)))
            {
                return false;
            }

            if (_exclude.Any(x => x.IsMatch(alert.Method)))
            {
                return false;
            }
        }

        if (Cooldown <= TimeSpan.Zero)
        {
            return true;
        }

        string key = $"{alert.Kind}|{alert.Monitor}|{alert.Method}|{alert.Resource}";
        DateTime now = JobClock.UtcNow;

        while (true)
        {
            if (_lastSent.TryGetValue(key, out DateTime last))
            {
                if (now - last < Cooldown)
                {
                    Interlocked.Increment(ref _suppressed);
                    return false;
                }

                if (_lastSent.TryUpdate(key, now, last))
                {
                    return true;
                }
            }
            else if (_lastSent.TryAdd(key, now))
            {
                return true;
            }
        }
    }

    private static void AddPatterns(List<Regex> target, string[] patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        foreach (string pattern in patterns)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pattern, nameof(patterns));
            target.Add(new Regex(
                "^" + Regex.Escape(pattern.Trim()).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$",
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking));
        }
    }
}

/// <summary>A sink that passes on only the alerts its <see cref="AlertRule"/> lets through.</summary>
/// <param name="inner">The sink that delivers the alerts.</param>
/// <param name="rule">The rule.</param>
public sealed class AlertRuleSink([NotNull] IAlertSink inner, [NotNull] AlertRule rule) : IAlertSink
{
    /// <summary>The sink that delivers the alerts.</summary>
    public IAlertSink Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>The rule.</summary>
    public AlertRule Rule { get; } = rule ?? throw new ArgumentNullException(nameof(rule));

    /// <inheritdoc />
    public void Publish(Alert alert)
    {
        if (Rule.Allows(alert))
        {
            Inner.Publish(alert);
        }
    }
}
