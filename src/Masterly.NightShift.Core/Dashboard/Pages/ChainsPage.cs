namespace Masterly.NightShift.Dashboard.Pages;

internal partial class ChainsPage
{
    // Without ranges the storage reads the whole set, so only the first chains are shown
    private const int MaxChainsWithoutRanges = 100;

    /// <summary>Reads one page of chains, newest first, leaving out the ones whose record expired.</summary>
    private List<JobChainInfo> ReadChains(int from, int perPage, out Pager? pager)
    {
        List<string> ids;
        pager = null;

        using (IStorageConnection connection = Storage.GetReadOnlyConnection())
        {
            if (connection is JobStorageConnection storageConnection && Storage.HasFeature(JobStorageFeatures.ExtendedApi))
            {
                long count = storageConnection.GetSetCount(JobChains.SetKey);
                pager = new Pager(from, perPage, DashboardOptions.DefaultRecordsPerPage, count);

                // The set is ordered by creation time, oldest first: the page is taken from the end
                long end = count - 1 - pager.FromRecord;
                long start = Math.Max(0, end - pager.RecordsPerPage + 1);
                ids = end < 0 ? [] : [.. Enumerable.Reverse(storageConnection.GetRangeFromSet(JobChains.SetKey, (int)start, (int)end))];
            }
            else
            {
                ids = [.. connection.GetAllItemsFromSet(JobChains.SetKey).Take(MaxChainsWithoutRanges)];
            }
        }

        return [.. ids.Select(Storage.GetChain).OfType<JobChainInfo>()];
    }

    internal static NonEscapedString StatusLabel(JobChainInfo chain)
    {
        ChainFlowStatus status = ChainFlowchart.StatusOf(chain);
        return new NonEscapedString($"<span class=\"label {ChainFlowchart.StatusCss(status)} text-uppercase\">{System.Net.WebUtility.HtmlEncode(ChainFlowchart.StatusText(status))}</span>");
    }
}
