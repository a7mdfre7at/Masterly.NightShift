namespace Masterly.NightShift.Dashboard.Pages;

internal partial class EnqueuedJobsPage(string queue) : RazorPage
{
    public string Queue { get; } = queue;
}
