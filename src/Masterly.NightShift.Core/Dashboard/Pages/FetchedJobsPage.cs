namespace Masterly.NightShift.Dashboard.Pages;

internal partial class FetchedJobsPage(string queue) : RazorPage
{
    public string Queue { get; } = queue;
}
