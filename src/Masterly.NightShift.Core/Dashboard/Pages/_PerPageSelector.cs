namespace Masterly.NightShift.Dashboard.Pages;

internal partial class PerPageSelector(Pager pager) : RazorPage
{
    private readonly Pager _pager = pager;

    private bool HasNextPage => _pager.CurrentPage < _pager.TotalPageCount;
}
