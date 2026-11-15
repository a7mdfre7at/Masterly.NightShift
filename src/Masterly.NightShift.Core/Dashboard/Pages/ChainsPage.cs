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

    /// <summary>The chain's status as a chip in the colours of the state it stands for.</summary>
    internal static NonEscapedString StatusLabel(HtmlHelper html, JobChainInfo chain)
    {
        return ChainFlowchart.StatusLabel(html, ChainFlowchart.StatusOf(chain));
    }

    /// <summary>How many steps the chain has, how many succeeded and failed, and how many finished in any way.</summary>
    internal static ChainStepCounts CountSteps(JobChainInfo chain)
    {
        ArgumentNullException.ThrowIfNull(chain);

        int succeeded = chain.Steps.Count(static x => SucceededState.StateName.Equals(x.StateOrOutcome, StringComparison.OrdinalIgnoreCase));
        int finished = chain.Steps.Count(static x => JobChains.IsSettledState(x.State));
        return new ChainStepCounts(chain.Steps.Count, succeeded, chain.FailedCount, finished);
    }
}

/// <summary>The steps of a chain by outcome (see <see cref="ChainsPage.CountSteps"/>).</summary>
/// <param name="Total">How many steps the chain has.</param>
/// <param name="Succeeded">How many succeeded.</param>
/// <param name="Failed">How many failed.</param>
/// <param name="Finished">How many finished in any way, or whose job expired.</param>
internal readonly record struct ChainStepCounts(int Total, int Succeeded, int Failed, int Finished)
{
    /// <summary>How many steps haven't finished.</summary>
    public int Pending => Total - Finished;

    /// <summary>The share of the steps that finished, from 0 to 100.</summary>
    public double Percent => Total == 0 ? 0 : 100.0 * Finished / Total;
}
