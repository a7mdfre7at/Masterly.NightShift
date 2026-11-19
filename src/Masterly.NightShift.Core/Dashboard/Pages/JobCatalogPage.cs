using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using Masterly.NightShift.Dashboard.Resources;

namespace Masterly.NightShift.Dashboard.Pages;

internal partial class JobCatalogPage : RazorPage
{
    // The filters a fact stands for; the others are shown by name
    private static readonly HashSet<string> FactFilters = new(StringComparer.Ordinal)
    {
        "Queue", "AutomaticRetry", "JobTimeout", "DisableConcurrentExecution", "Throttle", "RateLimit", "RunWindow",
    };

    // How a method runs, from its filters and its type's (the method's own win): its queue, retries, timeout,
    // concurrency and time window as chips, then the names of its other filters. Read from the attributes, no storage.
    private NonEscapedString Facts(JobCatalogEntry entry)
    {
        StringBuilder html = new();

        if (Find<QueueAttribute>(entry) is { } queue)
        {
            html.Append(Html.QueueChip(queue.Queue));
        }

        if (Find<AutomaticRetryAttribute>(entry) is { } retry)
        {
            AppendFact(html, "arrow-repeat", string.Format(CultureInfo.CurrentCulture, Strings.CatalogPage_Fact_Retries, retry.Attempts));
        }

        if (Find<JobTimeoutAttribute>(entry) is { } timeout)
        {
            AppendFact(html, "hourglass-split", string.Format(CultureInfo.CurrentCulture, Strings.CatalogPage_Fact_Timeout, Html.ToHumanDuration(timeout.Timeout, false)?.Trim()));
        }

        if (Find<DisableConcurrentExecutionAttribute>(entry) != null)
        {
            AppendFact(html, "lock-fill", Strings.CatalogPage_Fact_OneAtATime);
        }

        if (Find<ThrottleAttribute>(entry) is { } throttle)
        {
            AppendFact(html, "lock-fill", string.Format(CultureInfo.CurrentCulture, Strings.CatalogPage_Fact_AtMost, throttle.MaxConcurrency));
        }

        if (Find<RateLimitAttribute>(entry) is { } rate)
        {
            AppendFact(html, "lightning-fill", string.Format(CultureInfo.CurrentCulture, Strings.CatalogPage_Fact_RateLimit, rate.Limit, Html.ToHumanDuration(TimeSpan.FromSeconds(rate.PerSeconds), false)?.Trim()));
        }

        if (Find<RunWindowAttribute>(entry) is { } window)
        {
            AppendFact(html, "clock", string.Format(CultureInfo.CurrentCulture, Strings.CatalogPage_Fact_Window, window.Window));
        }

        foreach (string filter in entry.Filters)
        {
            if (!FactFilters.Contains(filter))
            {
                html.Append("<span class=\"ns-chip ns-chip-status ns-catalog-filter\">").Append(WebUtility.HtmlEncode(filter)).Append("</span>");
            }
        }

        return new NonEscapedString(html.ToString());
    }

    // A method's parameters, each with its type: "(int days, CancellationToken token)", a parameter the server
    // supplies in italics with its reason as the tooltip
    private static NonEscapedString Signature(JobCatalogEntry entry)
    {
        StringBuilder html = new("<span class=\"ns-catalog-params\">(");
        for (int i = 0; i < entry.Parameters.Count; i++)
        {
            JobCatalogParameter parameter = entry.Parameters[i];
            if (i > 0)
            {
                html.Append(", ");
            }

            html.Append("<span class=\"hf-param ").Append(parameter.Injected ? "hf-param-injected" : null).Append("\" title=\"")
                .Append(WebUtility.HtmlEncode(parameter.Injected ? Strings.CatalogPage_Injected : parameter.TypeName)).Append("\">")
                .Append("<span class=\"hf-param-type\">").Append(WebUtility.HtmlEncode(parameter.TypeName)).Append("</span> ")
                .Append(WebUtility.HtmlEncode(parameter.Name)).Append("</span>");
        }

        return new NonEscapedString(html.Append(")</span>").ToString());
    }

    // A type's heading: its namespace, quieter, then its name
    private static NonEscapedString TypeHeading(Type type)
    {
        string name = WebUtility.HtmlEncode(JobMethodCallRenderer.GetNameWithoutGenericArity(type));
        return new NonEscapedString(string.IsNullOrEmpty(type.Namespace)
            ? name
            : $"<span class=\"ns-catalog-namespace\">{WebUtility.HtmlEncode(type.Namespace)}.</span>{name}");
    }

    private void AppendFact(StringBuilder html, string icon, string text)
    {
        html.Append("<span class=\"ns-chip ns-chip-status\">").Append(Html.Icon(icon)).Append(' ').Append(WebUtility.HtmlEncode(text)).Append("</span>");
    }

    private static TAttribute? Find<TAttribute>(JobCatalogEntry entry)
        where TAttribute : Attribute
    {
        // Some filters may be applied more than once; the first one is shown
        return entry.Method.GetCustomAttributes<TAttribute>(inherit: true).FirstOrDefault()
            ?? entry.Type.GetCustomAttributes<TAttribute>(inherit: true).FirstOrDefault();
    }
}
