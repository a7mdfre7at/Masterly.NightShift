namespace Masterly.NightShift.Dashboard.Pages;

internal partial class Breadcrumbs(string title, IDictionary<string, string> items) : RazorPage
{
    public string Title { get; } = title;
    public IDictionary<string, string> Items { get; } = items;
}
