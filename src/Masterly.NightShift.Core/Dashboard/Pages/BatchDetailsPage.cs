namespace Masterly.NightShift.Dashboard.Pages;

internal partial class BatchDetailsPage(string id)
{
    public string Id { get; } = RouteValues.Decode(id);
}
