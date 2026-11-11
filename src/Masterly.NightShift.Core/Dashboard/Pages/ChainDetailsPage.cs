namespace Masterly.NightShift.Dashboard.Pages;

internal partial class ChainDetailsPage(string id)
{
    public string Id { get; } = RouteValues.Decode(id);
}
