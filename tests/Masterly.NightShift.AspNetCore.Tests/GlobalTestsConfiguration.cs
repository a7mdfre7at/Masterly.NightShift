using Xunit.v3;

// KeepAliveProcess.Status, GlobalConfiguration and JobStorage.Current are process-wide, so tests run one at a time
[assembly: Parallelization(MaxThreads = 1)]
