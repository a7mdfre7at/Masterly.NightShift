using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    public class JobCatalogFacts : IDisposable
    {
        public JobCatalogFacts()
        {
            JobCatalog.Clear();
        }

        public void Dispose()
        {
            JobCatalog.Clear();
        }

        [Fact, StaticLock]
        public void Add_ListsThePublicMethodsOfTheType_AndIgnoresGenericOrByRefOnes()
        {
            JobCatalog.Add<CatalogJobs>();

            var entries = JobCatalog.GetEntries().Where(e => e.Type == typeof(CatalogJobs)).ToList();
            var names = entries.Select(e => e.Method.Name).ToList();

            Assert.Contains("Import", names);
            Assert.Contains("Notify", names);
            Assert.Contains("StaticOne", names);
            Assert.DoesNotContain("Generic", names);
            Assert.DoesNotContain("WithOut", names);
            Assert.DoesNotContain("get_Property", names);
        }

        [Fact, StaticLock]
        public void Entries_DescribeParametersFiltersAndInjection()
        {
            JobCatalog.Add<CatalogJobs>();

            var entry = JobCatalog.GetEntries().Single(e => e.Type == typeof(CatalogJobs) && e.Method.Name == "Import");

            Assert.Equal(new[] { "file", "rows", "console", "token" }, entry.Parameters.Select(p => p.Name));
            Assert.Equal(new[] { false, false, true, true }, entry.Parameters.Select(p => p.Injected));
            Assert.Equal(new[] { "file", "rows" }, entry.UserParameters.Select(p => p.Name));
            Assert.Equal("int", entry.Parameters[1].TypeName);
            Assert.Equal("number", entry.Parameters[1].InputKind);
            Assert.Contains("AutomaticRetry", entry.Filters);
            Assert.Contains("JobTimeout", entry.Filters);
            Assert.True(entry.IsAsync);
            Assert.Null(entry.Invoker);
            Assert.Equal("JobCatalogFacts.CatalogJobs.Import", entry.InsightsKey);
        }

        [Fact, StaticLock]
        public void Add_IsIdempotent_AndFindLocatesEntriesById()
        {
            JobCatalog.Add(typeof(CatalogJobs));
            JobCatalog.Add(typeof(CatalogJobs));

            var entries = JobCatalog.GetEntries().Where(e => e.Type == typeof(CatalogJobs) && e.Method.Name == "Notify").ToList();

            var entry = Assert.Single(entries);
            Assert.Same(entry.Type, JobCatalog.Find(entry.Id).Type);
            Assert.Null(JobCatalog.Find("nope|x|"));
        }

        [Fact, StaticLock]
        public void CreateJob_ConvertsTextualArguments_ToTheParameterTypes()
        {
            JobCatalog.Add<CatalogJobs>();
            var entry = JobCatalog.GetEntries().Single(e => e.Type == typeof(CatalogJobs) && e.Method.Name == "Notify");

            var job = JobCatalog.CreateJob(entry, new Dictionary<string, string>
            {
                { "channel", "Sms" },
                { "urgent", "on" },
                { "when", "2026-11-04T18:30" },
                { "ratio", "0.5" },
                { "tags", "[\"a\",\"b\"]" },
            }, "critical");

            Assert.Equal(Channel.Sms, job.Args[0]);
            Assert.Equal(true, job.Args[1]);
            Assert.Equal(new DateTime(2026, 11, 4, 18, 30, 0), job.Args[2]);
            Assert.Equal(0.5, job.Args[3]);
            Assert.Equal(new[] { "a", "b" }, (string[])job.Args[4]);
            Assert.Equal("critical", job.Queue);
        }

        [Fact, StaticLock]
        public void CreateJob_LeavesInjectedParametersNull_AndUsesDefaults()
        {
            JobCatalog.Add<CatalogJobs>();
            var entry = JobCatalog.GetEntries().Single(e => e.Type == typeof(CatalogJobs) && e.Method.Name == "Import");

            var job = JobCatalog.CreateJob(entry, new Dictionary<string, string> { { "file", "a.csv" } });

            Assert.Equal("a.csv", job.Args[0]);
            Assert.Equal(10, job.Args[1]);
            Assert.Null(job.Args[2]);
            Assert.Null(job.Args[3]);
            Assert.Null(job.Queue);
        }

        [Fact, StaticLock]
        public void CreateJob_ReportsBadInput_WithTheParameterName()
        {
            JobCatalog.Add<CatalogJobs>();
            var entry = JobCatalog.GetEntries().Single(e => e.Type == typeof(CatalogJobs) && e.Method.Name == "Notify");

            var ex = Assert.Throws<ArgumentException>(() => JobCatalog.CreateJob(entry, new Dictionary<string, string> { { "channel", "Pigeon" } }));
            Assert.Contains("Pigeon", ex.Message);

            ex = Assert.Throws<ArgumentException>(() => JobCatalog.CreateJob(entry, new Dictionary<string, string> { { "channel", "Sms" }, { "ratio", "abc" } }));
            Assert.Contains("ratio", ex.Message);

            ex = Assert.Throws<ArgumentException>(() => JobCatalog.CreateJob(entry, new Dictionary<string, string>()));
            Assert.Contains("channel", ex.Message);
        }

        [Fact]
        public void FriendlyNamesAndInputKinds_CoverCommonTypes()
        {
            Assert.Equal("int?", JobCatalog.GetFriendlyName(typeof(int?)));
            Assert.Equal("List<string>", JobCatalog.GetFriendlyName(typeof(List<string>)));
            Assert.Equal("string[]", JobCatalog.GetFriendlyName(typeof(string[])));
            Assert.Equal("checkbox", JobCatalog.GetInputKind(typeof(bool?)));
            Assert.Equal("select", JobCatalog.GetInputKind(typeof(Channel)));
            Assert.Equal("datetime", JobCatalog.GetInputKind(typeof(DateTimeOffset)));
            Assert.Equal("text", JobCatalog.GetInputKind(typeof(Guid)));
            Assert.Equal("number", JobCatalog.GetInputKind(typeof(decimal)));
            Assert.Equal("json", JobCatalog.GetInputKind(typeof(List<string>)));
        }

        public enum Channel { Email, Sms }

        // Jobs are built with reflection on purpose: a lambda would make the source generator emit an invoker
        // for CatalogJobs in this assembly, and Entries_DescribeParametersFiltersAndInjection expects none.
        [Fact]
        public void CreateEntry_DescribesAnyJob_EvenOutsideTheCatalog()
        {
            var job = new Job(typeof(CatalogJobs), typeof(CatalogJobs).GetMethod("Notify"), Channel.Email, true, null, 0.5, new[] { "a" });

            var entry = JobCatalog.CreateEntry(job);

            Assert.Equal(typeof(CatalogJobs), entry.Type);
            Assert.Equal("Notify", entry.Method.Name);
            Assert.Equal(5, entry.Parameters.Count);
        }

        [Fact]
        public void FormatArguments_RendersValuesForTheRunForm()
        {
            var when = new DateTime(2026, 11, 5, 14, 30, 0, DateTimeKind.Utc);
            var job = new Job(typeof(CatalogJobs), typeof(CatalogJobs).GetMethod("Notify"), Channel.Email, true, when, 0.5, new[] { "a", "b" });

            var arguments = JobCatalog.FormatArguments(job);

            Assert.Equal(new[] { "Email", "true", "2026-11-05T14:30:00", "0.5", "[\"a\",\"b\"]" }, arguments.ToArray());
        }

        [Fact]
        public void FormatArguments_SkipsInjectedParameters_AndKeepsNulls()
        {
            var job = new Job(typeof(CatalogJobs), typeof(CatalogJobs).GetMethod("Import"), "file.csv", 3, null, CancellationToken.None);

            var arguments = JobCatalog.FormatArguments(job);

            Assert.Equal(new[] { "file.csv", "3", null, null }, arguments.ToArray());
        }

        [Fact]
        public void FormatArgument_RendersADateTimeOffset_AsItsOwnClockTime()
        {
            var value = new DateTimeOffset(2026, 11, 5, 16, 30, 15, TimeSpan.FromHours(2));

            var text = JobCatalog.FormatArgument(value, typeof(DateTimeOffset?));

            Assert.Equal("2026-11-05T16:30:15", text);
        }

        [Fact]
        public void FormatArgument_RendersNull_AsAnEmptyString()
        {
            Assert.Equal(String.Empty, JobCatalog.FormatArgument(null, typeof(string)));
        }

        [Fact]
        public void FormatDefault_RendersEachKindOfValue()
        {
            Assert.Equal(String.Empty, JobCatalog.FormatDefault(null));
            Assert.Equal("false", JobCatalog.FormatDefault(false));
            Assert.Equal("text", JobCatalog.FormatDefault("text"));
            Assert.Equal("Sms", JobCatalog.FormatDefault(Channel.Sms));
            Assert.Equal("1.5", JobCatalog.FormatDefault(1.5m));
            Assert.Equal("[1,2]", JobCatalog.FormatDefault(new[] { 1, 2 }));
        }

        public class CatalogJobs
        {
            public string Property { get; set; }

            [AutomaticRetry(Attempts = 2), JobTimeout(30)]
            public Task Import(string file, int rows = 10, IJobConsole console = null, CancellationToken token = default) => Task.CompletedTask;

            public void Notify(Channel channel, bool urgent, DateTime? when, double ratio, string[] tags) { }

            public static void StaticOne() { }

            public void Generic<T>(T value) { }

            public void WithOut(out int value) { value = 0; }
        }
    }
}
