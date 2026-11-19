using Masterly.NightShift.Dashboard.Resources;

namespace Masterly.NightShift.Dashboard.Pages;

internal partial class BatchDetailsPage(string id) : RazorPage
{
    public string Id { get; } = RouteValues.Decode(id);

    /// <summary>The built-in state whose colours and icon the batch's status chip takes.</summary>
    internal static string StatusState(JobBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return batch.IsFinished ? SucceededState.StateName : batch.IsStuck ? FailedState.StateName : ProcessingState.StateName;
    }

    /// <summary>The batch's status as a chip: finished, failed (stuck) or running.</summary>
    internal static NonEscapedString StatusLabel(HtmlHelper html, JobBatch batch)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(batch);

        string text = batch.IsFinished ? Strings.BatchesPage_State_Finished : batch.IsStuck ? Strings.BatchesPage_State_Failed : Strings.BatchesPage_State_Running;
        return html.StateLabel(StatusState(batch), text);
    }

    /// <summary>The share of the batch's jobs that are no longer pending (succeeded, failed or deleted), from 0 to 100.</summary>
    internal static double FinishedPercent(JobBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return batch.Total <= 0 ? 0 : Math.Min(100, 100.0 * (batch.Total - batch.Pending) / batch.Total);
    }
}
