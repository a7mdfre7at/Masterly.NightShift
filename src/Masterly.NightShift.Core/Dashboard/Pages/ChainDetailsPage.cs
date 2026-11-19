namespace Masterly.NightShift.Dashboard.Pages;

internal partial class ChainDetailsPage(string id) : RazorPage
{
    public string Id { get; } = RouteValues.Decode(id);
}
