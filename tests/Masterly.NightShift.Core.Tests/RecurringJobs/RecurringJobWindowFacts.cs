using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Masterly.NightShift.Client;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    /// <summary>
    /// <see cref="RecurringJobOptions.StartAt"/> and <see cref="RecurringJobOptions.EndAt"/>, through the real manager
    /// and scheduler over an in-memory storage.
    /// </summary>
    public class RecurringJobWindowFacts
    {
        private const string Id = "window-job";
        private const string Daily = "0 9 * * *";

        // .NET Framework doesn't know IANA time zone ids
#if NETFRAMEWORK
        private const string BerlinTimeZoneId = "W. Europe Standard Time";
#else
        private const string BerlinTimeZoneId = "Europe/Berlin";
#endif

        private readonly Dictionary<string, Dictionary<string, string>> _hashes = new Dictionary<string, Dictionary<string, string>>();
        private readonly Dictionary<string, double> _schedule = new Dictionary<string, double>();
        private readonly List<DateTime> _runs = new List<DateTime>();
        private readonly Mock<JobStorage> _storage = new Mock<JobStorage>();
        private readonly Mock<IStorageConnection> _connection = new Mock<IStorageConnection>();
        private readonly Mock<IWriteOnlyTransaction> _transaction = new Mock<IWriteOnlyTransaction>();
        private readonly Mock<IBackgroundJobFactory> _factory = new Mock<IBackgroundJobFactory>();
        private readonly BackgroundProcessContextMock _context = new BackgroundProcessContextMock();
        private int _hashWrites;

        // Monday, 2 March 2026, 08:00 UTC
        private DateTime _now = new DateTime(2026, 3, 2, 8, 0, 0, DateTimeKind.Utc);

        public RecurringJobWindowFacts()
        {
            _storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
            _context.Storage = _storage;

            _connection.Setup(x => x.GetAllEntriesFromHash(It.IsAny<string>()))
                .Returns<string>(key => _hashes.TryGetValue(key, out var hash) ? new Dictionary<string, string>(hash) : null);
            _connection.Setup(x => x.GetAllItemsFromSet("recurring-jobs"))
                .Returns(() => new HashSet<string>(_schedule.Keys));
            _connection.Setup(x => x.GetFirstByLowestScoreFromSet("recurring-jobs", It.IsAny<double>(), It.IsAny<double>()))
                .Returns<string, double, double>((_, from, to) => _schedule
                    .Where(x => x.Value >= from && x.Value <= to)
                    .OrderBy(x => x.Value)
                    .Select(x => x.Key)
                    .FirstOrDefault());
            _connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);

            _transaction
                .Setup(x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()))
                .Callback<string, IEnumerable<KeyValuePair<string, string>>>((key, pairs) =>
                {
                    _hashWrites++;
                    if (!_hashes.TryGetValue(key, out var hash)) _hashes[key] = hash = new Dictionary<string, string>();
                    foreach (var pair in pairs) hash[pair.Key] = pair.Value;
                });
            _transaction
                .Setup(x => x.AddToSet("recurring-jobs", It.IsAny<string>(), It.IsAny<double>()))
                .Callback<string, string, double>((_, value, score) => _schedule[value] = score);

            _factory.SetupGet(x => x.StateMachine).Returns(new Mock<IStateMachine>().Object);
            _factory.Setup(x => x.Create(It.IsAny<CreateContext>()))
                .Returns<CreateContext>(ctx =>
                {
                    var time = JobHelper.FromTimestamp((long)ctx.Parameters["Time"]);
                    _runs.Add(time);
                    return new BackgroundJob("job-" + _runs.Count, ctx.Job, _now);
                });
        }

        private Dictionary<string, string> Hash => _hashes["recurring-job:" + Id];

        private DateTime? NextExecution => JobHelper.DeserializeNullableDateTime(Hash.TryGetValue("NextExecution", out var value) ? value : null);

        private static DateTime Utc(int day, int hour, int minute = 0, int second = 0)
        {
            return new DateTime(2026, 3, day, hour, minute, second, DateTimeKind.Utc);
        }

        private static DateTimeOffset At(int day, int hour, int minute = 0)
        {
            return new DateTimeOffset(Utc(day, hour, minute));
        }

        private RecurringJobManager CreateManager()
        {
            return new RecurringJobManager(_storage.Object, _factory.Object, new DefaultTimeZoneResolver(), () => _now);
        }

        private void Register(
            DateTimeOffset? startAt,
            DateTimeOffset? endAt,
            MisfireHandlingMode misfireHandling = MisfireHandlingMode.Relaxed,
            string cron = Daily,
            TimeZoneInfo timeZone = null)
        {
            CreateManager().AddOrUpdate(Id, Job.FromExpression(() => Method()), cron, new RecurringJobOptions
            {
                StartAt = startAt,
                EndAt = endAt,
                MisfireHandling = misfireHandling,
                TimeZone = timeZone ?? TimeZoneInfo.Utc,
            });
        }

        private List<DateTime> RunSchedulerAt(DateTime now)
        {
            _now = now;
            _runs.Clear();

            var scheduler = new RecurringJobScheduler(_factory.Object, TimeSpan.FromMilliseconds(100), new DefaultTimeZoneResolver(), () => _now);
            scheduler.Execute(_context.Object);

            return _runs.ToList();
        }

        [Fact]
        public void AddOrUpdate_WithAStartInTheFuture_SchedulesTheFirstOccurrenceAfterIt()
        {
            Register(At(5, 0), null);

            Assert.Equal(Utc(5, 9), NextExecution);
            Assert.Equal(JobHelper.ToTimestamp(Utc(5, 9)), _schedule[Id]);
            Assert.Equal(Utc(5, 0), JobHelper.DeserializeDateTime(Hash["StartAt"]));
            Assert.False(Hash.ContainsKey("EndAt"));
        }

        [Fact]
        public void AddOrUpdate_WithAStartExactlyOnAnOccurrence_SchedulesThatOccurrence()
        {
            Register(At(4, 9), null);

            Assert.Equal(Utc(4, 9), NextExecution);
        }

        [Fact]
        public void AddOrUpdate_KeepsTheRecurringJobVersion_SoEarlierServersKeepSchedulingIt()
        {
            Register(At(4, 0), At(20, 0));

            Assert.Equal("2", Hash["V"]);
        }

        [Fact]
        public void AddOrUpdate_StoresTheWindowInUtc_WhateverTheOffset()
        {
            Register(new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.FromHours(2)), null);

            Assert.Equal(Utc(4, 22), JobHelper.DeserializeDateTime(Hash["StartAt"]));
            Assert.Equal(Utc(5, 9), NextExecution);
        }

        [Fact]
        public void Scheduler_CreatesNoRun_BeforeTheStart_AndRunsFromIt()
        {
            Register(At(5, 0), null);

            Assert.Empty(RunSchedulerAt(Utc(3, 9, 0, 5)));
            Assert.Empty(RunSchedulerAt(Utc(4, 9, 0, 5)));

            var runs = RunSchedulerAt(Utc(5, 9, 0, 5));

            Assert.Equal(new[] { Utc(5, 9, 0, 5) }, runs);
            Assert.Equal(Utc(6, 9), NextExecution);
        }

        [Theory]
        [InlineData(MisfireHandlingMode.Relaxed, 1)]
        [InlineData(MisfireHandlingMode.Strict, 2)]
        [InlineData(MisfireHandlingMode.Ignorable, 0)]
        public void Scheduler_AfterDowntime_NeverCatchesUpOnOccurrencesBeforeTheStart(MisfireHandlingMode mode, int expectedRuns)
        {
            // Occurrences on 2, 3 and 4 March are before the start; 5 and 6 March were missed
            Register(At(4, 12), null, mode);

            var runs = RunSchedulerAt(Utc(6, 10));

            Assert.Equal(expectedRuns, runs.Count);
            Assert.All(runs, run => Assert.True(run >= Utc(4, 12), $"{run:o} is before the start"));
            Assert.Equal(Utc(7, 9), NextExecution);
        }

        [Fact]
        public void Scheduler_InStrictMode_CreatesOnlyTheOccurrencesFromTheStart()
        {
            Register(At(4, 12), null, MisfireHandlingMode.Strict);

            var runs = RunSchedulerAt(Utc(6, 10));

            Assert.Equal(new[] { Utc(5, 9), Utc(6, 9) }, runs);
        }

        [Fact]
        public void Scheduler_RunsTheOccurrenceAtTheEnd_ThenLeavesTheScheduleButKeepsTheJob()
        {
            Register(null, At(4, 9));

            Assert.Single(RunSchedulerAt(Utc(2, 9, 0, 5)));
            Assert.Single(RunSchedulerAt(Utc(3, 9, 0, 5)));
            Assert.Single(RunSchedulerAt(Utc(4, 9, 0, 5)));

            Assert.Null(NextExecution);
            Assert.Equal(String.Empty, Hash["NextExecution"]);
            Assert.Equal(-1.0D, _schedule[Id]);
            Assert.Equal(Utc(4, 9, 0, 5), JobHelper.DeserializeDateTime(Hash["LastExecution"]));

            Assert.Empty(RunSchedulerAt(Utc(5, 9, 0, 5)));
            Assert.Empty(RunSchedulerAt(Utc(20, 9, 0, 5)));
            Assert.Contains(Id, _connection.Object.GetAllItemsFromSet("recurring-jobs"));
        }

        [Fact]
        public void Scheduler_InStrictMode_AfterDowntime_CreatesOnlyTheOccurrencesUpToTheEnd()
        {
            Register(null, At(4, 12), MisfireHandlingMode.Strict);

            var runs = RunSchedulerAt(Utc(7, 10));

            Assert.Equal(new[] { Utc(2, 9), Utc(3, 9), Utc(4, 9) }, runs);
            Assert.Null(NextExecution);
            Assert.Equal(-1.0D, _schedule[Id]);
        }

        [Fact]
        public void Scheduler_InRelaxedMode_AfterDowntime_CreatesOneRunForTheMissedOccurrencesInTheWindow()
        {
            Register(null, At(4, 12));

            var runs = RunSchedulerAt(Utc(7, 10));

            Assert.Equal(new[] { Utc(7, 10) }, runs);
            Assert.Null(NextExecution);
        }

        [Fact]
        public void Scheduler_InRelaxedMode_CreatesNoRun_WhenEveryMissedOccurrenceIsAfterTheEnd()
        {
            // Ran on 3 March; the 4 March occurrence is after the end, but an earlier server, which doesn't know
            // the window, scheduled it anyway
            Register(null, At(4, 8));
            RunSchedulerAt(Utc(2, 9));
            RunSchedulerAt(Utc(3, 9));

            _hashes["recurring-job:" + Id]["NextExecution"] = JobHelper.SerializeDateTime(Utc(4, 9));
            _schedule[Id] = JobHelper.ToTimestamp(Utc(4, 9));

            var runs = RunSchedulerAt(Utc(7, 10));

            Assert.Empty(runs);
            Assert.Null(NextExecution);
        }

        [Fact]
        public void Scheduler_InIgnorableMode_RunsTheOccurrenceAtTheEnd_AndDropsMissedOnes()
        {
            Register(null, At(4, 9), MisfireHandlingMode.Ignorable);

            Assert.Equal(new[] { Utc(4, 9) }, RunSchedulerAt(Utc(4, 9)));
            Assert.Null(NextExecution);
        }

        [Fact]
        public void Scheduler_InIgnorableMode_AfterDowntime_CreatesNoRun_AndLeavesTheSchedule()
        {
            Register(null, At(4, 12), MisfireHandlingMode.Ignorable);

            Assert.Empty(RunSchedulerAt(Utc(7, 10)));
            Assert.Null(NextExecution);
        }

        [Fact]
        public void Scheduler_RespectsTheWindow_InATimeZoneAcrossTheSpringChange()
        {
            // Berlin moves from 02:00 CET to 03:00 CEST on 29 March 2026, so 02:30 that day runs at 03:00 CEST
            var berlin = TimeZoneInfo.FindSystemTimeZoneById(BerlinTimeZoneId);
            _now = Utc(28, 12);

            Register(
                new DateTimeOffset(2026, 3, 29, 0, 0, 0, TimeSpan.FromHours(1)),
                new DateTimeOffset(2026, 3, 30, 2, 30, 0, TimeSpan.FromHours(2)),
                cron: "30 2 * * *",
                timeZone: berlin);

            Assert.Equal(Utc(29, 1), NextExecution);

            Assert.Single(RunSchedulerAt(Utc(29, 1)));
            Assert.Equal(Utc(30, 0, 30), NextExecution);

            Assert.Single(RunSchedulerAt(Utc(30, 0, 30)));
            Assert.Null(NextExecution);
        }

        [Fact]
        public void AddOrUpdate_WithAChangedWindow_RecomputesTheNextExecution_AndClearingItRestoresTheSchedule()
        {
            Register(null, null);
            Assert.Equal(Utc(2, 9), NextExecution);

            Register(At(10, 0), null);
            Assert.Equal(Utc(10, 9), NextExecution);
            Assert.Equal(Utc(2, 8), JobHelper.DeserializeDateTime(Hash["ScheduleChangedAt"]));

            Register(null, null);
            Assert.Equal(Utc(2, 9), NextExecution);
            Assert.Equal(String.Empty, Hash["StartAt"]);
            Assert.Equal(JobHelper.ToTimestamp(Utc(2, 9)), _schedule[Id]);
        }

        [Fact]
        public void AddOrUpdate_ClearingAnEndThatPassed_PutsTheJobBackOnTheSchedule()
        {
            Register(null, At(2, 9));
            RunSchedulerAt(Utc(2, 9));
            Assert.Null(NextExecution);

            _now = Utc(5, 12);
            Register(null, null);

            Assert.Equal(String.Empty, Hash["EndAt"]);
            Assert.Equal(Utc(6, 9), NextExecution);
            Assert.Equal(JobHelper.ToTimestamp(Utc(6, 9)), _schedule[Id]);
        }

        [Fact]
        public void AddOrUpdate_WithTheSameWindow_WritesNothing_EvenBelowTheStoredPrecision()
        {
            var startAt = At(10, 0).AddTicks(1234);
            Register(startAt, At(20, 0).AddTicks(5678));
            var writes = _hashWrites;

            Register(startAt, At(20, 0).AddTicks(5678));

            Assert.Equal(writes, _hashWrites);
        }

        [Fact]
        public void AddOrUpdate_WithAnEndThatAlreadyPassed_RegistersTheJob_WithoutASchedule()
        {
            _now = Utc(10, 12);

            Register(null, At(5, 0));

            Assert.Null(NextExecution);
            Assert.Equal(-1.0D, _schedule[Id]);
            Assert.Empty(RunSchedulerAt(Utc(11, 9)));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void AddOrUpdate_ThrowsArgumentException_WhenTheEndIsNotLaterThanTheStart(int endMinusStartInMinutes)
        {
            var exception = Assert.Throws<ArgumentException>(() => Register(At(5, 0), At(5, 0).AddMinutes(endMinusStartInMinutes)));

            Assert.Equal("options", exception.ParamName);
            Assert.Empty(_hashes);
        }

        [Fact]
        public void AddOrUpdate_ThrowsArgumentOutOfRangeException_WhenTheStartIsBefore1970()
        {
            var exception = Assert.Throws<ArgumentOutOfRangeException>(
                () => Register(new DateTimeOffset(1969, 12, 31, 0, 0, 0, TimeSpan.Zero), null));

            Assert.Equal("options", exception.ParamName);
            Assert.Empty(_hashes);
        }

        [Fact]
        public void AddOrUpdate_ThrowsArgumentOutOfRangeException_WhenTheEndIsBefore1970()
        {
            var exception = Assert.Throws<ArgumentOutOfRangeException>(
                () => Register(null, DateTimeOffset.MinValue));

            Assert.Equal("options", exception.ParamName);
        }

        [Fact]
        public void AddOrUpdate_AcceptsTheLargestEnd()
        {
            Register(null, DateTimeOffset.MaxValue);

            Assert.Equal(Utc(2, 9), NextExecution);
            Assert.Equal(Utc(2, 9), RunSchedulerAt(Utc(2, 9)).Single());
            Assert.Equal(Utc(3, 9), NextExecution);
        }

        [Fact]
        public void TriggerJob_WorksAfterTheEnd_AndKeepsTheJobOffTheSchedule()
        {
            Register(null, At(2, 9));
            RunSchedulerAt(Utc(2, 9));
            _now = Utc(5, 12);

            var jobId = CreateManager().TriggerJob(Id);

            Assert.NotNull(jobId);
            Assert.Equal(Utc(5, 12), _runs.Last());
            Assert.Null(NextExecution);
            Assert.Equal(-1.0D, _schedule[Id]);
        }

        [Fact]
        public void TriggerJob_WorksBeforeTheStart_AndKeepsTheFirstOccurrenceAfterIt()
        {
            Register(At(10, 0), null);

            var jobId = CreateManager().TriggerJob(Id);

            Assert.NotNull(jobId);
            Assert.Equal(Utc(2, 8), _runs.Single());
            Assert.Equal(Utc(10, 9), NextExecution);
        }

        [Fact]
        public void Resume_BeforeTheStart_SchedulesTheFirstOccurrenceAfterIt()
        {
            Register(At(10, 0), null);
            var manager = CreateManager();
            manager.Pause(Id);
            Assert.Null(NextExecution);

            manager.Resume(Id);

            Assert.Equal(Utc(10, 9), NextExecution);
        }

        [Fact]
        public void UpdateSchedule_KeepsTheWindow()
        {
            Register(At(10, 0), At(20, 0));

            CreateManager().UpdateSchedule(Id, "0 12 * * *");

            Assert.Equal(Utc(10, 12), NextExecution);
            Assert.Equal(Utc(10, 0), JobHelper.DeserializeDateTime(Hash["StartAt"]));
            Assert.Equal(Utc(20, 0), JobHelper.DeserializeDateTime(Hash["EndAt"]));
        }

        [Fact]
        public void GetRecurringJobs_ReturnsTheWindow_InUtc()
        {
            Register(new DateTimeOffset(2026, 3, 10, 2, 0, 0, TimeSpan.FromHours(2)), At(20, 0));

            var dto = _connection.Object.GetRecurringJobs().Single();

            Assert.Equal(Utc(10, 0), dto.StartAt);
            Assert.Equal(DateTimeKind.Utc, dto.StartAt.Value.Kind);
            Assert.Equal(Utc(20, 0), dto.EndAt);
        }

        [Fact]
        public void GetRecurringJobs_ReturnsNoWindow_ForAJobWithoutOne_OrWithAClearedOrDamagedOne()
        {
            Register(null, null);
            Hash["EndAt"] = String.Empty;
            Hash["StartAt"] = "not a date";

            var dto = _connection.Object.GetRecurringJobs().Single();

            Assert.Null(dto.StartAt);
            Assert.Null(dto.EndAt);
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Method() { }
    }
}
