namespace Masterly.NightShift.Dashboard.Pages;

internal partial class InlineMetric(DashboardMetric dashboardMetric) : RazorPage
{
    public DashboardMetric DashboardMetric { get; } = dashboardMetric;
}
