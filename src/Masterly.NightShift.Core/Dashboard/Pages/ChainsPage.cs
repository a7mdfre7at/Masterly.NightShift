namespace Masterly.NightShift.Dashboard.Pages;

internal partial class ChainsPage
{
    // Without ranges the storage reads the whole set, which isn't ordered: at most this many chains are read
    private const int MaxChainsWithoutRanges = 100;

    /// <summary>
    /// Reads one page of chains, newest first, leaving out the ones whose record expired (a server's chain
    /// maintenance removes them from the list a moment after). Each chain is read as a summary: its steps'
    /// states and its first step's method only.
    /// </summary>
    private List<JobChainInfo> ReadChains(int from, int perPage, out Pager? pager)
    {
        pager = null;

        using IStorageConnection connection = Storage.GetReadOnlyConnection();

        List<string> ids;
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
            // A storage without ranges can't say which chains are the newest: some chains are shown, newest first
            ids = [.. connection.GetAllItemsFromSet(JobChains.SetKey).Take(MaxChainsWithoutRanges)];
        }

        List<JobChainInfo> chains = [.. ids.Select(id => JobChains.ReadSummary(connection, id)).OfType<JobChainInfo>()];
        return pager == null ? [.. chains.OrderByDescending(static x => x.CreatedAt)] : chains;
    }

    internal static NonEscapedString StatusLabel(JobChainInfo chain)
    {
        ChainFlowStatus status = ChainFlowchart.StatusOf(chain);
        return new NonEscapedString($"<span class=\"label {ChainFlowchart.StatusCss(status)} text-uppercase\">{System.Net.WebUtility.HtmlEncode(ChainFlowchart.StatusText(status))}</span>");
    }
}
