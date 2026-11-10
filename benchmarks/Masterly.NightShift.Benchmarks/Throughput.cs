using System.Diagnostics;
using System.Globalization;

namespace NightShiftBenchmarks;

/// <summary>
/// End to end: enqueues jobs into SQL Server, starts a real server and times it until every job has succeeded.
/// The libraries take turns, and the first round of each is dropped because it includes JIT and schema work.
/// </summary>
internal static class Throughput
{
    public static int Run(string[] args)
    {
        int jobCount = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 5000;
        int rounds = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 6;
        int workerCount = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 20;
        int enqueueThreads = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 1;

        HangfireSide.Configure();
        NightShiftSide.Configure();
        ILibrary[] libraries = [new HangfireSide(), new NightShiftSide(), new NightShiftSide(bulk: true)];

        // BENCH_ONLY=NightShift runs only the libraries whose name starts with it
        string? only = Environment.GetEnvironmentVariable("BENCH_ONLY");
        if (!string.IsNullOrEmpty(only))
        {
            libraries = [.. libraries.Where(x => x.Name.StartsWith(only, StringComparison.OrdinalIgnoreCase))];
        }
        Dictionary<string, List<(double Enqueue, double Process)>> results = [];

        foreach (ILibrary library in libraries)
        {
            library.OpenDatabase();
            results[library.Name] = [];
            Console.WriteLine($"{library.Name}: SQL client {library.Driver}");
        }

        Console.WriteLine($".NET {Environment.Version}, {jobCount} jobs, {workerCount} workers, {enqueueThreads} enqueue threads, {rounds} rounds (first dropped)");
        for (int round = 0; round < rounds; round++)
        {
            foreach (ILibrary library in libraries)
            {
                (double enqueue, double process) = RunRound(library, jobCount, workerCount, enqueueThreads);
                Console.WriteLine($"round {round} {library.Name}: enqueue {enqueue:F0}/s, process {process:F0}/s");
                if (round > 0)
                {
                    results[library.Name].Add((enqueue, process));
                }
            }
        }

        foreach ((string name, List<(double Enqueue, double Process)> samples) in results)
        {
            string enqueue = Summary(samples.Select(static x => x.Enqueue));
            string process = Summary(samples.Select(static x => x.Process));
            Console.WriteLine($"RESULT {name}: enqueue {enqueue}; process {process}");
        }

        return 0;
    }

    private static (double Enqueue, double Process) RunRound(ILibrary library, int jobCount, int workerCount, int enqueueThreads)
    {
        library.Reset();
        Completed.Reset();
        Stopwatch enqueue = Stopwatch.StartNew();
        if (enqueueThreads > 1)
        {
            library.EnqueueCountedInParallel(jobCount, enqueueThreads);
        }
        else
        {
            library.EnqueueAllCounted(jobCount);
        }

        enqueue.Stop();
        Stopwatch process = Stopwatch.StartNew();
        using (library.StartServer(workerCount))
        {
            // The jobs count themselves: asking the database in a loop would slow down what is being measured.
            // The database is only asked at the end, for the last jobs' state changes.
            while (Completed.Count < jobCount || library.SucceededCount() < jobCount)
            {
                if (process.Elapsed > TimeSpan.FromMinutes(5))
                {
                    throw new TimeoutException($"{library.Name} did not finish {jobCount} jobs in 5 minutes.");
                }

                Thread.Sleep(Completed.Count < jobCount ? 1 : 5);
            }

            process.Stop();
        }

        return (jobCount / enqueue.Elapsed.TotalSeconds, jobCount / process.Elapsed.TotalSeconds);
    }

    private static string Summary(IEnumerable<double> values)
    {
        List<double> sorted = [.. values.Order()];
        return $"median {sorted[sorted.Count / 2]:F0}/s (min {sorted[0]:F0}, max {sorted[^1]:F0})";
    }
}
