namespace Masterly.NightShift.Dashboard.Pages
{
    /// <summary>
    /// The layout of the dashboard pages: the navigation bar, the sidebar, the footer and the page's title. Dashboard
    /// extensions use it as the layout of their pages.
    /// </summary>
    partial class LayoutPage
    {
        public LayoutPage(string title)
        {
            Title = title;
        }

        public string Title { get; }
    }
}
