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
            if (DisplayNameType == null)
            {
                return;
            }

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

    /// <summary>
    /// Renders a state chip: the state's icon and <paramref name="text"/> in the state's MD3 colour group
    /// (<c>ns-state-chip ns-state-{group}</c>, see <see cref="JobHistoryRenderer.GetStateColorGroup"/>). A state
    /// without a group gets the neutral chip, with the class <c>label-state-{suffix}</c> when it has a CSS suffix.
    /// </summary>
    /// <param name="stateName">The name of the state, which chooses the colours and the icon.</param>
    /// <param name="text">The text of the chip.</param>
    /// <param name="hover">Whether the chip turns into the filled state colour when the link around it is hovered.</param>
    public NonEscapedString StateLabel(string? stateName, string? text, bool hover = false)
    {
        if (string.IsNullOrWhiteSpace(stateName))
        {
            return Raw($"<em>{HtmlEncode(Strings.Common_NoState)}</em>");
        }

        string? group = JobHistoryRenderer.GetStateColorGroup(stateName);
        string? cssSuffix = group == null ? JobHistoryRenderer.GetStateCssSuffix(stateName) : null;

        StringBuilder css = new("ns-state-chip");
        if (group != null)
        {
            css.Append(" ns-state-").Append(group);
        }
        else if (cssSuffix != null)
        {
            css.Append(" label-state-").Append(cssSuffix);
        }

        if (hover)
        {
            css.Append(" label-hover");
        }

        return Raw($"<span class=\"{HtmlEncode(css.ToString())}\">{Icon(JobHistoryRenderer.GetStateIcon(stateName))}<span>{HtmlEncode(text)}</span></span>");
    }

    /// <summary>
    /// Renders the selection checkbox of a job list's row (<c>js-jobs-list-checkbox</c>, posted as <c>jobs[]</c>),
    /// named "Select job #id" for screen readers.
    /// </summary>
    /// <param name="jobId">The id of the job, the value posted when the box is checked.</param>
    public NonEscapedString JobCheckbox([NotNull] string jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        string shown = Guid.TryParse(jobId, out _) ? string.Concat(jobId.AsSpan(0, 8), "…") : $"#{jobId}";
        return JobCheckbox(jobId, string.Format(CultureInfo.CurrentCulture, Strings.Common_SelectJob, shown));
    }

    /// <summary>
    /// Renders the selection checkbox of a list's row, like <see cref="JobCheckbox(string)"/>, with the accessible name
    /// given (for a list of other items, such as recurring jobs).
    /// </summary>
    /// <param name="value">The value posted as <c>jobs[]</c> when the box is checked.</param>
    /// <param name="label">The accessible name of the checkbox.</param>
    public NonEscapedString JobCheckbox([NotNull] string value, [NotNull] string label)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(label);
        return Raw($"<input type=\"checkbox\" class=\"js-jobs-list-checkbox\" name=\"jobs[]\" value=\"{HtmlEncode(value)}\" aria-label=\"{HtmlEncode(label)}\"/>");
    }

    /// <summary>
    /// Renders the checkbox in the header of a job list that selects every row of the page
    /// (<c>js-jobs-list-select-all</c>), named "Select all jobs on this page".
    /// </summary>
    public NonEscapedString SelectAllCheckbox()
    {
        return SelectAllCheckbox(Strings.Common_SelectAllJobs);
    }

    /// <summary>Renders the select-all checkbox of a list, like <see cref="SelectAllCheckbox()"/>, with the accessible name given.</summary>
    /// <param name="label">The accessible name of the checkbox.</param>
    public NonEscapedString SelectAllCheckbox([NotNull] string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        return Raw($"<input type=\"checkbox\" class=\"js-jobs-list-select-all\" aria-label=\"{HtmlEncode(label)}\"/>");
    }

    /// <summary>
    /// Renders the attributes that make a command button (<c>data-ajax</c>, a list command or a cluster command) ask
    /// first in the dashboard's confirmation dialog: <c>data-confirm</c> (the headline), and when given
    /// <c>data-confirm-body</c>, <c>data-confirm-label</c>, <c>data-confirm-destructive</c> and
    /// <c>data-confirm-type</c>. A headline may hold <c>{0}</c>, replaced by the number of selected rows.
    /// </summary>
    /// <param name="title">The headline: a question that names the action, such as "Delete 3 jobs?".</param>
    /// <param name="body">What happens, or <c>null</c>.</param>
    /// <param name="confirmLabel">The label of the confirming button, or <c>null</c> for the command button's text.</param>
    /// <param name="destructive">Whether the confirming button has the error colour.</param>
    /// <param name="typeToConfirm">Text the user must type before the confirming button works, or <c>null</c>.</param>
    public NonEscapedString ConfirmAttributes(
        [NotNull] string title,
        [CanBeNull] string? body = null,
        [CanBeNull] string? confirmLabel = null,
        bool destructive = false,
        [CanBeNull] string? typeToConfirm = null)
    {
        ArgumentNullException.ThrowIfNull(title);

        StringBuilder html = new();
        html.Append("data-confirm=\"").Append(HtmlEncode(title)).Append('"');
        if (!string.IsNullOrEmpty(body))
        {
            html.Append(" data-confirm-body=\"").Append(HtmlEncode(body)).Append('"');
        }

        if (!string.IsNullOrEmpty(confirmLabel))
        {
            html.Append(" data-confirm-label=\"").Append(HtmlEncode(confirmLabel)).Append('"');
        }

        if (destructive)
        {
            html.Append(" data-confirm-destructive=\"true\"");
        }

        if (!string.IsNullOrEmpty(typeToConfirm))
        {
            html.Append(" data-confirm-type=\"").Append(HtmlEncode(typeToConfirm)).Append('"');
        }

        return Raw(html.ToString());
    }

    /// <summary>
    /// Renders an icon of the dashboard's icon sprite (Bootstrap Icons) as an inline SVG that takes the size and color
    /// of the text around it. The icon is hidden from screen readers: when it is all a button or link shows, give that
    /// element an accessible name (<c>aria-label</c> or <c>title</c>).
    /// </summary>
    /// <param name="name">
    /// The name of a Bootstrap Icon the sprite holds, such as <c>"trash-fill"</c> or <c>"arrow-repeat"</c> (a
    /// <c>bi-</c> prefix and white space around the name are ignored). A Glyphicon name the dashboard used before,
    /// such as <c>"trash"</c> or <c>"glyphicon-trash"</c>, gives the icon that replaced it. Any other name is written
    /// as it is and draws nothing: the sprite holds only the icons listed in the documentation. The script's
    /// <c>NightShift.icon(name)</c> resolves names the same way.
    /// </param>
    public NonEscapedString Icon([NotNull] string name)
    {
        return Icon(name, null);
    }

    /// <summary>
    /// Renders an icon of the dashboard's icon sprite as an inline SVG, like <see cref="Icon(string)"/>, with more
    /// CSS classes on the <c>svg</c> element, such as <c>"text-success"</c>.
    /// </summary>
    /// <param name="name">The name of the icon, as for <see cref="Icon(string)"/>.</param>
    /// <param name="cssClass">Classes added to <c>bi</c>, or <c>null</c>.</param>
    public NonEscapedString Icon([NotNull] string name, [CanBeNull] string? cssClass)
    {
        ArgumentNullException.ThrowIfNull(name);

        string symbol = DashboardIcons.SymbolOf(name);
        string css = string.IsNullOrWhiteSpace(cssClass) ? "bi" : $"bi {cssClass}";

        return Raw($"<svg class=\"{HtmlEncode(css)}\" aria-hidden=\"true\"><use href=\"{HtmlEncode(Page.Url.To(DashboardIcons.Path))}#bi-{HtmlEncode(symbol)}\"></use></svg>");
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
        if (duration == null)
        {
            return null;
        }

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
            : $"<span class=\"badge text-bg-danger\"><i>{HtmlEncode(Strings.Common_Unknown)}</i></span>";

        return new NonEscapedString(label);
    }

    public NonEscapedString ServerId(string? serverId)
    {
        // State data written without a server id (by another writer, or before ServerId replaced ServerName)
        if (serverId == null)
        {
            return new NonEscapedString($"<span class=\"badge text-bg-secondary\"><i>{HtmlEncode(Strings.Common_Unknown)}</i></span>");
        }

        string[] parts = serverId.Split(':');
        string shortenedId = parts.Length > 1
            ? string.Join(":", parts.Take(parts.Length - 1))
            : serverId;

        return new NonEscapedString(
            $"<span class=\"badge text-bg-secondary text-uppercase\" title=\"{HtmlEncode(serverId)}\">{HtmlEncode(shortenedId)}</span>");
    }

    private static readonly StackTraceHtmlFragments StackTraceHtmlFragments = new()
    {
        BeforeFrame = "<span class='st-frame'>",
        AfterFrame = "</span>",
        BeforeType = "<span class='st-type'>",
        AfterType = "</span>",
        BeforeMethod = "<span class='st-method'>",
        AfterMethod = "</span>",
        BeforeParameters = "<span class='st-params'>",
        AfterParameters = "</span>",
        BeforeParameterType = "<span class='st-param'><span class='st-param-type'>",
        AfterParameterType = "</span>",
        BeforeParameterName = "<span class='st-param-name'>",
        AfterParameterName = "</span></span>",
        BeforeFile = "<span class='st-file'>",
        AfterFile = "</span>",
        BeforeLine = "<span class='st-line'>",
        AfterLine = "</span>",
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
