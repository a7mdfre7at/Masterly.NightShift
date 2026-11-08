using System.Threading.Tasks;
using Masterly.NightShift;

namespace CSharp73Check
{
    // Every invoker shape the generator writes: instance and static, void and Task, with arguments
    [GenerateJobInvokers]
    public class ReportJob
    {
        public void Run(int day, string name)
        {
        }

        public Task RunAsync(int day)
        {
            return Task.CompletedTask;
        }

        public static void Cleanup()
        {
        }
    }

    public static class Jobs
    {
        public static void Create(IBackgroundJobClient client)
        {
            client.Enqueue<ReportJob>(x => x.Run(1, "daily"));
            client.Enqueue<ReportJob>(x => x.RunAsync(2));
            client.Enqueue(() => ReportJob.Cleanup());
        }
    }
}
