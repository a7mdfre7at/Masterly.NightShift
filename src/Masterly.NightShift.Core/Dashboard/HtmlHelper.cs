// This file is part of Hangfire. Copyright © 2013-2014 Hangfire OÜ.
// 
// Hangfire is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as 
// published by the Free Software Foundation, either version 3 
// of the License, or any later version.
// 
// Hangfire is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
// 
// You should have received a copy of the GNU Lesser General Public 
// License along with Hangfire. If not, see <http://www.gnu.org/licenses/>.

using System.ComponentModel;
using System.Globalization;
using System.Linq.Expressions;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;

namespace Masterly.NightShift.Dashboard;

/// <summary>
/// Renders the common parts of dashboard pages (job names and links, state labels, times, pagers, metrics) as HTML,
/// for pages and dashboard extensions.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "We use instance methods in this class for better observability.")]
public class HtmlHelper
{
    private static readonly Type? DisplayNameType;
    private static readonly Func<object, string>? GetDisplayName;

    static HtmlHelper()
    {
        try
        {
            DisplayNameType = typeof(DisplayNameAttribute);
            if (DisplayNameType == null) return;

            ParameterExpression p = Expression.Parameter(typeof(object));
            UnaryExpression converted = Expression.Convert(p, DisplayNameType);

            GetDisplayName = Expression.Lambda<Func<object, string>>(Expression.Call(converted, "get_DisplayName", null), p).Compile();
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            // Ignoring
        }
    }

    public HtmlHelper([NotNull] RazorPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        Page = page;
    }

    public RazorPage Page { get; }

    public NonEscapedString Breadcrumbs(string title, [NotNull] IDictionary<string, string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return RenderPartial(new Breadcrumbs(title, items));
    }

    public NonEscapedString JobsSidebar()
    {
        return RenderPartial(new SidebarMenu(JobsSidebarMenu.Items));
    }

    public NonEscapedString SidebarMenu([NotNull] IEnumerable<Func<RazorPage, MenuItem>> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return RenderPartial(new SidebarMenu(items));
    }

    public NonEscapedString BlockMetric([NotNull] DashboardMetric metric)
    {
        ArgumentNullException.ThrowIfNull(metric);
        return RenderPartial(new BlockMetric(metric));
    }

    public NonEscapedString InlineMetric([NotNull] DashboardMetric metric)
    {
        ArgumentNullException.ThrowIfNull(metric);
        return RenderPartial(new InlineMetric(metric));
    }

    public NonEscapedString Paginator([NotNull] Pager pager)
    {
        ArgumentNullException.ThrowIfNull(pager);
        return RenderPartial(new Paginator(pager));
    }

    public NonEscapedString PerPageSelector([NotNull] Pager pager)
    {
        ArgumentNullException.ThrowIfNull(pager);
        return RenderPartial(new PerPageSelector(pager));
    }

    public NonEscapedString RenderPartial(RazorPage partialPage)
    {
        partialPage.Assign(Page);
        return new NonEscapedString(partialPage.ToString());
    }

    public NonEscapedString Raw(string? value)
    {
        return new NonEscapedString(value ?? string.Empty);
    }

    public NonEscapedString JobId(string jobId, bool shorten = true)
    {
        return new NonEscapedString(HtmlEncode(Guid.TryParse(jobId, out Guid guid)
            ? (shorten ? string.Concat(jobId.AsSpan(0, 8), "…") : jobId)
            : $"#{jobId}"));
    }

    public string JobName(Job? job)
    {
        return JobName(job, includeQueue: true);
    }

    public string JobName(Job? job, bool includeQueue)
    {
        if (job == null)
        {
            return Strings.Common_CannotFindTargetMethod;
        }

        JobDisplayNameAttribute? jobDisplayNameAttribute = job.Method.GetCustomAttribute<JobDisplayNameAttribute>();
        if (jobDisplayNameAttribute != null)
        {
            try
            {
                return jobDisplayNameAttribute.Format(Page.Context, job);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                return jobDisplayNameAttribute.DisplayName;
            }
        }

        if (DisplayNameType != null && GetDisplayName != null)
        {
            Attribute? attribute = job.Method.GetCustomAttribute(DisplayNameType);
            if (attribute != null)
            {
                try
                {
                    return string.Format(
                        CultureInfo.CurrentCulture,
                        GetDisplayName(attribute),
                        RedactedAttribute.GetDisplayArguments(job));
                }
                catch (FormatException)
                {
                    return GetDisplayName(attribute);
                }
            }
        }

        Func<DashboardContext, Job, string>? displayNameProvider = Page.DashboardOptions.DisplayNameFunc;
        if (displayNameProvider != null)
        {
            try
            {
                return displayNameProvider(Page.Context, job);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                // Ignoring exceptions
            }
        }

        return job.ToString(includeQueue);
    }

    public NonEscapedString StateLabel(string? stateName)
    {
        return StateLabel(stateName, stateName);
    }

    public NonEscapedString StateLabel(string? stateName, string? text, bool hover = false)
    {
        if (string.IsNullOrWhiteSpace(stateName))
        {
            return Raw($"<em>{HtmlEncode(Strings.Common_NoState)}</em>");
        }

        string? cssSuffix = JobHistoryRenderer.GetStateCssSuffix(stateName);
        string? cssHover = hover ? "label-hover" : null;

        if (cssSuffix != null)
        {
            return Raw($"<span class=\"label label-default {cssHover} label-state-{HtmlEncode(cssSuffix)}\">{HtmlEncode(text)}</span>");
        }

        // No suffix: the neutral label look
        return Raw($"<span class=\"label label-default {cssHover}\">{HtmlEncode(text)}</span>");
    }

    public NonEscapedString JobIdLink(string jobId)
    {
        return Raw($"<a href=\"{HtmlEncode(Page.Url.JobDetails(jobId))}\">{JobId(jobId)}</a>");
    }

    public NonEscapedString JobNameLink(string jobId, Job? job)
    {
        return JobNameLink(jobId, job, includeQueue: true);
    }

    public NonEscapedString JobNameLink(string jobId, Job? job, bool includeQueue)
    {
        return Raw($"<a class=\"job-method\" href=\"{HtmlEncode(Page.Url.JobDetails(jobId))}\">{HtmlEncode(JobName(job, includeQueue))}</a>");
    }

    public NonEscapedString RelativeTime(DateTime value)
    {
        return Raw($"<span data-moment=\"{HtmlEncode(JobHelper.ToTimestamp(value).ToString(CultureInfo.InvariantCulture))}\">{HtmlEncode(value.ToString(CultureInfo.CurrentCulture))}</span>");
    }

    public NonEscapedString MomentTitle(DateTime time, string? value)
    {
        return Raw($"<span data-moment-title=\"{HtmlEncode(JobHelper.ToTimestamp(time).ToString(CultureInfo.InvariantCulture))}\">{HtmlEncode(value)}</span>");
    }

    public NonEscapedString LocalTime(DateTime value)
    {
        return Raw($"<span data-moment-local=\"{HtmlEncode(JobHelper.ToTimestamp(value).ToString(CultureInfo.InvariantCulture))}\">{HtmlEncode(value.ToString(CultureInfo.CurrentCulture))}</span>");
    }

    public string? ToHumanDuration(TimeSpan? duration, bool displaySign = true)
    {
        if (duration == null) return null;

        StringBuilder builder = new();
        if (displaySign)
        {
            builder.Append(duration.Value.TotalMilliseconds < 0 ? "-" : "+");
        }

        duration = duration.Value.Duration();

        if (duration.Value.Days > 0)
        {
            builder.Append(FormattableString.Invariant($"{duration.Value.Days}d "));
        }

        if (duration.Value.Hours > 0)
        {
            builder.Append(FormattableString.Invariant($"{duration.Value.Hours}h "));
        }

        if (duration.Value.Minutes > 0)
        {
            builder.Append(FormattableString.Invariant($"{duration.Value.Minutes}m "));
        }

        if (duration.Value.TotalHours < 1)
        {
            if (duration.Value.Seconds > 0)
            {
                builder.Append(duration.Value.Seconds);
                if (duration.Value.Milliseconds > 0)
                {
                    builder.Append(FormattableString.Invariant($".{duration.Value.Milliseconds.ToString(CultureInfo.InvariantCulture).PadLeft(3, '0')}"));
                }

                builder.Append("s ");
            }
            else
            {
                if (duration.Value.Milliseconds > 0)
                {
                    builder.Append(FormattableString.Invariant($"{duration.Value.Milliseconds}ms "));
                }
            }
        }

        if (builder.Length <= 1)
        {
            builder.Append(" <1ms ");
        }

        builder.Remove(builder.Length - 1, 1);

        return builder.ToString();
    }

    public NonEscapedString QueueLabel(string queue)
    {
        string label = queue != null 
            ? $"<a href=\"{HtmlEncode(Page.Url.Queue(queue))}\">{HtmlEncode(queue)}</a>" 
            : $"<span class=\"label label-danger\"><i>{HtmlEncode(Strings.Common_Unknown)}</i></span>";

        return new NonEscapedString(label);
    }

    public NonEscapedString ServerId(string serverId)
    {
        string[] parts = serverId.Split(':');
        string shortenedId = parts.Length > 1
            ? string.Join(":", parts.Take(parts.Length - 1))
            : serverId;

        return new NonEscapedString(
            $"<span class=\"labe label-defult text-uppercase\" title=\"{HtmlEncode(serverId)}\">{HtmlEncode(shortenedId)}</span>");
    }

    private static readonly StackTraceHtmlFragments StackTraceHtmlFragments = new()
    {
        BeforeFrame         = "<span class='st-frame'>"                            , AfterFrame         = "</span>",
        BeforeType          = "<span class='st-type'>"                             , AfterType          = "</span>",
        BeforeMethod        = "<span class='st-method'>"                           , AfterMethod        = "</span>",
        BeforeParameters    = "<span class='st-params'>"                           , AfterParameters    = "</span>",
        BeforeParameterType = "<span class='st-param'><span class='st-param-type'>", AfterParameterType = "</span>",
        BeforeParameterName = "<span class='st-param-name'>"                       , AfterParameterName = "</span></span>",
        BeforeFile          = "<span class='st-file'>"                             , AfterFile          = "</span>",
        BeforeLine          = "<span class='st-line'>"                             , AfterLine          = "</span>",
    };

    public NonEscapedString StackTrace(string? stackTrace)
    {
        try
        {
            string html = StackTraceFormatter.FormatHtml(stackTrace, StackTraceHtmlFragments);

            // Text without any stack frame, such as a bare error message, comes back empty: show it as it is
            return new NonEscapedString(string.IsNullOrWhiteSpace(html) ? HtmlEncode(stackTrace) : html);
        }
        catch (RegexMatchTimeoutException)
        {
            return new NonEscapedString(HtmlEncode(stackTrace));
        }
    }

    public string HtmlEncode(string? text)
    {
        return WebUtility.HtmlEncode(text) ?? string.Empty;
    }
}
