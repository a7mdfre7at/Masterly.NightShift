using Xunit.v3;

[assembly: Parallelization(MaxThreads = 1)]

#if NETFRAMEWORK
[assembly: Xunit.AssemblyFixture(typeof(Masterly.NightShift.Core.Tests.LateBoundLoggingFixture))]
#endif
