using System.Reflection;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Dashboard.Resources;

namespace Masterly.NightShift.Core.Tests.Dashboard;

public class DashboardMetricsFacts
{
    [Fact]
    public void TheEnqueuedCount_HasATitleOfItsOwn_NotTheOneOfTheCountThatIsNullWhenEmpty()
    {
        Assert.Equal("Metrics_EnqueuedCount", DashboardMetrics.EnqueuedCount.Title);
        Assert.NotEqual(DashboardMetrics.EnqueuedCountOrNull.Title, DashboardMetrics.EnqueuedCount.Title);
        Assert.Equal(Strings.Metrics_EnqueuedCount, Strings.ResourceManager.GetString(DashboardMetrics.EnqueuedCount.Title));
    }

    [Fact]
    public void EveryBuiltInMetric_HasATitleTheStringsTranslate()
    {
        List<DashboardMetric> metrics = [.. typeof(DashboardMetrics)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(static x => x.FieldType == typeof(DashboardMetric))
            .Select(static x => (DashboardMetric)x.GetValue(null)!)];

        Assert.Contains(DashboardMetrics.EnqueuedCount, metrics);
        foreach (DashboardMetric metric in metrics)
        {
            Assert.False(string.IsNullOrEmpty(Strings.ResourceManager.GetString(metric.Title)), metric.Name);
        }
    }
}
