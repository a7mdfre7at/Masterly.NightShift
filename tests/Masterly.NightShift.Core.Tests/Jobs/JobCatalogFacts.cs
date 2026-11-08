using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.Dashboard;
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
            Assert.Equal("checkbox", JobCatalog.GetInputKind(typeof(bool)));
            // A checkbox can't say null
            Assert.Equal("select", JobCatalog.GetInputKind(typeof(bool?)));
            Assert.Equal("select", JobCatalog.GetInputKind(typeof(Channel)));
            Assert.Equal("datetime", JobCatalog.GetInputKind(typeof(DateTimeOffset)));
            Assert.Equal("text", JobCatalog.GetInputKind(typeof(Guid)));
            Assert.Equal("number", JobCatalog.GetInputKind(typeof(decimal)));
            Assert.Equal("json", JobCatalog.GetInputKind(typeof(List<string>)));
        }

        [Fact]
        public void CreateJob_TakesPlainGuidTimeSpanAndCharValues()
        {
            var entry = LookupEntry();

            var job = JobCatalog.CreateJob(entry, new Dictionary<string, string>
            {
                { "id", "3f2c1b6e-0d5a-4c1e-9b7a-2f9d1e6c4a10" },
                { "delay", "00:30:00" },
                { "separator", ";" },
                { "otherId", " {3f2c1b6e-0d5a-4c1e-9b7a-2f9d1e6c4a10} " },
                { "timeout", "1.02:00:00" },
            });

            Assert.Equal(new Guid("3f2c1b6e-0d5a-4c1e-9b7a-2f9d1e6c4a10"), job.Args[0]);
            Assert.Equal(TimeSpan.FromMinutes(30), job.Args[1]);
            Assert.Equal(';', job.Args[2]);
            Assert.Equal(new Guid("3f2c1b6e-0d5a-4c1e-9b7a-2f9d1e6c4a10"), job.Args[3]);
            Assert.Equal(new TimeSpan(1, 2, 0, 0), job.Args[4]);
        }

        [Fact]
        public void CreateJob_StillTakesGuidTimeSpanAndChar_AsQuotedJsonStrings()
        {
            var entry = LookupEntry();

            var job = JobCatalog.CreateJob(entry, new Dictionary<string, string>
            {
                { "id", "\"3f2c1b6e-0d5a-4c1e-9b7a-2f9d1e6c4a10\"" },
                { "delay", "\"00:30:00\"" },
                { "separator", "\"\\u0022\"" },
            });

            Assert.Equal(new Guid("3f2c1b6e-0d5a-4c1e-9b7a-2f9d1e6c4a10"), job.Args[0]);
            Assert.Equal(TimeSpan.FromMinutes(30), job.Args[1]);
            Assert.Equal('"', job.Args[2]);
            Assert.Null(job.Args[3]);
            Assert.Null(job.Args[4]);
        }

        [Theory]
        [InlineData("id", "not-a-guid")]
        [InlineData("delay", "half an hour")]
        [InlineData("delay", "3600")]
        [InlineData("separator", "ab")]
        public void CreateJob_ReportsABadGuidTimeSpanOrChar_WithTheParameterName(string name, string text)
        {
            var arguments = new Dictionary<string, string>
            {
                { "id", "3f2c1b6e-0d5a-4c1e-9b7a-2f9d1e6c4a10" },
                { "delay", "00:30:00" },
                { "separator", ";" },
            };
            arguments[name] = text;

            var exception = Assert.Throws<ArgumentException>(() => JobCatalog.CreateJob(LookupEntry(), arguments));

            Assert.StartsWith($"Parameter '{name}'", exception.Message);
        }

        [Fact]
        public void CreateJob_ReadsNull_ForNullableGuidAndTimeSpan()
        {
            var job = JobCatalog.CreateJob(LookupEntry(), new Dictionary<string, string>
            {
                { "id", "3f2c1b6e-0d5a-4c1e-9b7a-2f9d1e6c4a10" },
                { "delay", "00:30:00" },
                { "separator", ";" },
                { "otherId", "null" },
                { "timeout", " null " },
            });

            Assert.Null(job.Args[3]);
            Assert.Null(job.Args[4]);
        }

        [Fact]
        public void FormatArguments_GivesPlainGuidTimeSpanAndChar_ThatCreateJobReadsBack()
        {
            var id = new Guid("3f2c1b6e-0d5a-4c1e-9b7a-2f9d1e6c4a10");
            var job = new Job(typeof(CatalogJobs), typeof(CatalogJobs).GetMethod("Lookup"), id, TimeSpan.FromMinutes(30), ';', null, TimeSpan.FromSeconds(5));

            var arguments = JobCatalog.FormatArguments(job);
            var parameters = typeof(CatalogJobs).GetMethod("Lookup").GetParameters();
            var again = JobCatalog.CreateJob(JobCatalog.CreateEntry(job), parameters.Select((p, i) => (p.Name, arguments[i])).ToDictionary(x => x.Name, x => x.Item2));

            Assert.Equal(new[] { "3f2c1b6e-0d5a-4c1e-9b7a-2f9d1e6c4a10", "00:30:00", ";", "", "00:00:05" }, arguments.ToArray());
            Assert.Equal(job.Args, again.Args);
        }

        private static JobCatalogEntry LookupEntry()
        {
            return JobCatalog.CreateEntry(new Job(typeof(CatalogJobs), typeof(CatalogJobs).GetMethod("Lookup"), Guid.Empty, TimeSpan.Zero, 'x', null, null));
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

            Assert.Equal(new[] { "Email", "true", "2026-11-05T14:30:00Z", "0.5", "[\"a\",\"b\"]" }, arguments.ToArray());
        }

        [Fact]
        public void FormatArguments_SkipsInjectedParameters_AndKeepsNulls()
        {
            var job = new Job(typeof(CatalogJobs), typeof(CatalogJobs).GetMethod("Import"), "file.csv", 3, null, CancellationToken.None);

            var arguments = JobCatalog.FormatArguments(job);

            Assert.Equal(new[] { "file.csv", "3", null, null }, arguments.ToArray());
        }

        [Fact]
        public void FormatArgument_RendersADateTimeOffset_WithItsOffset()
        {
            // Without the offset, the run form gave back a different instant, read in the server's time zone
            var value = new DateTimeOffset(2026, 11, 5, 16, 30, 15, TimeSpan.FromHours(2));

            var text = JobCatalog.FormatArgument(value, typeof(DateTimeOffset?));

            Assert.Equal("2026-11-05T16:30:15+02:00", text);
        }

        [Fact]
        public void FormatArgument_RendersDateTimes_WithTheirKind()
        {
            Assert.Equal("2026-11-05T16:30:15Z", JobCatalog.FormatArgument(new DateTime(2026, 11, 5, 16, 30, 15, DateTimeKind.Utc), typeof(DateTime)));
            Assert.Equal("2026-11-05T16:30:15", JobCatalog.FormatArgument(new DateTime(2026, 11, 5, 16, 30, 15, DateTimeKind.Unspecified), typeof(DateTime)));
            Assert.Equal("2026-11-05T16:30:15.25", JobCatalog.FormatArgument(new DateTime(2026, 11, 5, 16, 30, 15, 250, DateTimeKind.Unspecified), typeof(DateTime)));
        }

        public static IEnumerable<object[]> Offsets()
        {
            yield return new object[] { new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.FromHours(5)) };
            yield return new object[] { new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.FromHours(-7.5)) };
            yield return new object[] { new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero) };
            yield return new object[] { new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.FromHours(3)).AddTicks(1234567) };
        }

        [Theory]
        [MemberData(nameof(Offsets))]
        public void RunAgain_GivesTheSameDateTimeOffset_WhenTheFormIsSentAsItIs(DateTimeOffset original)
        {
            var job = Job.FromExpression<CatalogDateJobs>(x => x.At(original, null));
            var entry = JobCatalog.CreateEntry(job);
            var formatted = JobCatalog.FormatArguments(job);

            var again = JobCatalog.CreateJob(entry, new Dictionary<string, string> { { "when", formatted[0] }, { "maybe", formatted[1] } });

            var value = Assert.IsType<DateTimeOffset>(again.Args[0]);
            Assert.Equal(original.UtcTicks, value.UtcTicks);
            Assert.Equal(original.Offset, value.Offset);
            Assert.Null(again.Args[1]);
        }

        [Theory]
        [InlineData(DateTimeKind.Utc)]
        [InlineData(DateTimeKind.Unspecified)]
        [InlineData(DateTimeKind.Local)]
        public void RunAgain_GivesTheSameDateTime_AndKind_WhenTheFormIsSentAsItIs(DateTimeKind kind)
        {
            var original = new DateTime(2026, 11, 8, 10, 0, 0, kind).AddTicks(70);
            var job = Job.FromExpression<CatalogDateJobs>(x => x.On(original, original));
            var entry = JobCatalog.CreateEntry(job);
            var formatted = JobCatalog.FormatArguments(job);

            var again = JobCatalog.CreateJob(entry, new Dictionary<string, string> { { "when", formatted[0] }, { "maybe", formatted[1] } });

            Assert.Equal(original, again.Args[0]);
            Assert.Equal(kind, ((DateTime)again.Args[0]).Kind);
            Assert.Equal(original, again.Args[1]);
            Assert.Equal(kind, ((DateTime)again.Args[1]).Kind);
        }

        [Theory]
        [InlineData("2026-11-04T18:30", 18, 30)]
        [InlineData("2026-11-04T18:30:00", 18, 30)]
        [InlineData("2026-11-04", 0, 0)]
        public void CreateJob_ReadsADateTimeOffsetWithoutAnOffset_AsUtc(string text, int hour, int minute)
        {
            // What the datetime-local input sends; before, the server's own time zone decided which moment it was
            var entry = JobCatalog.CreateEntry(Job.FromExpression<CatalogDateJobs>(x => x.At(DateTimeOffset.MinValue, null)));

            var job = JobCatalog.CreateJob(entry, new Dictionary<string, string> { { "when", text }, { "maybe", text } });

            Assert.Equal(new DateTimeOffset(2026, 11, 4, hour, minute, 0, TimeSpan.Zero), job.Args[0]);
            Assert.Equal(TimeSpan.Zero, ((DateTimeOffset)job.Args[0]).Offset);
            Assert.Equal(new DateTimeOffset(2026, 11, 4, hour, minute, 0, TimeSpan.Zero), job.Args[1]);
        }

        [Theory]
        [InlineData("2026-11-04T18:30+05:00", 5)]
        [InlineData("2026-11-04T18:30:00Z", 0)]
        [InlineData(" \"2026-11-04T18:30:00-03:00\" ", -3)]
        public void CreateJob_KeepsTheOffsetOfADateTimeOffset(string text, int offsetHours)
        {
            var entry = JobCatalog.CreateEntry(Job.FromExpression<CatalogDateJobs>(x => x.At(DateTimeOffset.MinValue, null)));

            var job = JobCatalog.CreateJob(entry, new Dictionary<string, string> { { "when", text } });

            Assert.Equal(new DateTimeOffset(2026, 11, 4, 18, 30, 0, TimeSpan.FromHours(offsetHours)), job.Args[0]);
            Assert.Equal(TimeSpan.FromHours(offsetHours), ((DateTimeOffset)job.Args[0]).Offset);
        }

        [Fact]
        public void CreateJob_ReadsADateTimeEndingInZ_AsUtc()
        {
            var entry = JobCatalog.CreateEntry(Job.FromExpression<CatalogDateJobs>(x => x.On(DateTime.MinValue, null)));

            var job = JobCatalog.CreateJob(entry, new Dictionary<string, string> { { "when", "2026-11-04T18:30:00Z" } });

            Assert.Equal(DateTimeKind.Utc, ((DateTime)job.Args[0]).Kind);
            Assert.Equal(new DateTime(2026, 11, 4, 18, 30, 0, DateTimeKind.Utc), job.Args[0]);
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

        [Fact, StaticLock]
        public void Add_LeavesOutObjectOverrides_AndWhatTheCompilerAddsToARecord()
        {
            JobCatalog.Add<CatalogRecord>();
            JobCatalog.Add<CatalogJobsWithOverrides>();

            var names = JobCatalog.GetEntries()
                .Where(e => e.Type == typeof(CatalogRecord) || e.Type == typeof(CatalogJobsWithOverrides))
                .Select(e => e.Type.Name + "." + e.Method.Name)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(new[] { "CatalogJobsWithOverrides.Run", "CatalogRecord.Build" }, names);
        }

        [Fact, CleanSerializerSettings]
        public void CreateJob_RefusesATypeName_InAnObjectArgument_WithRecommendedSettings()
        {
            GlobalConfiguration.Configuration.UseRecommendedSerializerSettings();
            var entry = InputEntry(nameof(CatalogInputJobs.Handle));
            var text = $"{{\"$type\":\"{PayloadTypeName}\",\"Name\":\"x\"}}";

            var ex = Assert.Throws<ArgumentException>(() => JobCatalog.CreateJob(entry, new Dictionary<string, string> { { "payload", text } }));

            Assert.Contains("payload", ex.Message);
            Assert.Contains("$type", ex.Message);
        }

        [Fact, CleanSerializerSettings]
        public void CreateJob_RefusesATypeName_InAnObjectMemberOfAnArgument_WithRecommendedSettings()
        {
            GlobalConfiguration.Configuration.UseRecommendedSerializerSettings();
            var entry = InputEntry(nameof(CatalogInputJobs.Wrap));
            var text = $"{{\"Data\":{{\"$type\":\"{PayloadTypeName}\",\"Name\":\"x\"}}}}";

            var ex = Assert.Throws<ArgumentException>(() => JobCatalog.CreateJob(entry, new Dictionary<string, string> { { "wrapper", text } }));

            Assert.Contains("$type", ex.Message);
        }

        [Fact, CleanSerializerSettings]
        public void CreateJob_RefusesATypeArgument_InsteadOfResolvingTheName()
        {
            var entry = InputEntry(nameof(CatalogInputJobs.Load));

            var ex = Assert.Throws<ArgumentException>(() => JobCatalog.CreateJob(entry, new Dictionary<string, string> { { "type", $"\"{TypeNameOf(typeof(CatalogPayload))}\"" } }));

            Assert.Contains("type", ex.Message);
            Assert.Contains("System.Type", ex.Message);
        }

        [Fact, CleanSerializerSettings]
        public void CreateJob_RefusesATypeMemberOfAnArgument()
        {
            var entry = InputEntry(nameof(CatalogInputJobs.LoadWrapped));

            var ex = Assert.Throws<ArgumentException>(() => JobCatalog.CreateJob(entry, new Dictionary<string, string> { { "wrapper", $"{{\"Kind\":\"{TypeNameOf(typeof(CatalogPayload))}\"}}" } }));

            Assert.Contains("System.Type", ex.Message);
        }

        [Fact, CleanSerializerSettings]
        public void StoredTypeArguments_AreStillReadBack()
        {
            var serialized = SerializationHelper.Serialize(typeof(CatalogPayload), SerializationOption.User);

            Assert.Equal(typeof(CatalogPayload), SerializationHelper.Deserialize<Type>(serialized, SerializationOption.User));
        }

        [Fact, CleanSerializerSettings]
        public void CreateJob_ReadsAnObjectArgument_AsPlainJson_WithRecommendedSettings()
        {
            GlobalConfiguration.Configuration.UseRecommendedSerializerSettings();
            var entry = InputEntry(nameof(CatalogInputJobs.Handle));

            var number = JobCatalog.CreateJob(entry, new Dictionary<string, string> { { "payload", "5" } });
            var element = JobCatalog.CreateJob(entry, new Dictionary<string, string> { { "payload", "{\"Name\":\"x\"}" } });

            Assert.Equal(5L, number.Args[0]);
            Assert.Equal("x", Assert.IsType<JsonElement>(element.Args[0]).GetProperty("Name").GetString());
        }

        [Fact, CleanSerializerSettings]
        public void CreateJob_StillTakesATypeName_ForAnInterfaceArgument_WhenItNamesAnImplementation()
        {
            var entry = InputEntry(nameof(CatalogInputJobs.Send));
            var good = $"{{\"$type\":\"{TypeNameOf(typeof(CatalogMessage))}\",\"Text\":\"hi\"}}";
            var bad = $"{{\"$type\":\"{PayloadTypeName}\",\"Name\":\"x\"}}";

            var job = JobCatalog.CreateJob(entry, new Dictionary<string, string> { { "message", good } });

            Assert.Equal("hi", Assert.IsType<CatalogMessage>(job.Args[0]).Text);
            Assert.Throws<ArgumentException>(() => JobCatalog.CreateJob(entry, new Dictionary<string, string> { { "message", bad } }));
        }

        [Fact, CleanSerializerSettings]
        public void FormatArguments_ThenCreateJob_RoundTripsObjectAndInterfaceArguments_WithRecommendedSettings()
        {
            GlobalConfiguration.Configuration.UseRecommendedSerializerSettings();
            var job = new Job(typeof(CatalogInputJobs), typeof(CatalogInputJobs).GetMethod(nameof(CatalogInputJobs.Mixed)),
                new CatalogPayload { Name = "x" }, new CatalogMessage { Text = "hi" });

            var texts = JobCatalog.FormatArguments(job);
            var rerun = JobCatalog.CreateJob(JobCatalog.CreateEntry(job), new Dictionary<string, string> { { "payload", texts[0] }, { "message", texts[1] } });

            Assert.DoesNotContain("$type", texts[0]);
            Assert.Equal("x", Assert.IsType<JsonElement>(rerun.Args[0]).GetProperty("Name").GetString());
            Assert.Equal("hi", Assert.IsType<CatalogMessage>(rerun.Args[1]).Text);
        }

        [Fact, CleanSerializerSettings]
        public void FormatArguments_ThenCreateJob_RoundTripsAnInterfaceArgument_WithDefaultSettings()
        {
            var job = new Job(typeof(CatalogInputJobs), typeof(CatalogInputJobs).GetMethod(nameof(CatalogInputJobs.Send)), new CatalogMessage { Text = "hi" });

            var texts = JobCatalog.FormatArguments(job);
            var rerun = JobCatalog.CreateJob(JobCatalog.CreateEntry(job), new Dictionary<string, string> { { "message", texts[0] } });

            Assert.Equal("hi", Assert.IsType<CatalogMessage>(rerun.Args[0]).Text);
        }

        private static readonly string PayloadTypeName = TypeNameOf(typeof(CatalogPayload));

        [Fact]
        public void GetFormField_OffersNullForNullableBoolsAndEnums_AndAMultipleChoiceForFlags()
        {
            var entry = FormEntry(x => x.Run(true, null, null, BindingFlags.Default));

            var maybe = JsonDocument.Parse(DashboardJson.Serialize(JobCatalog.GetFormField(entry.Parameters[1], "", false))).RootElement;
            var day = JsonDocument.Parse(DashboardJson.Serialize(JobCatalog.GetFormField(entry.Parameters[2], "", false))).RootElement;
            var flags = JsonDocument.Parse(DashboardJson.Serialize(JobCatalog.GetFormField(entry.Parameters[3], "Instance, Public", false))).RootElement;

            Assert.Equal("select", maybe.GetProperty("input").GetString());
            Assert.True(maybe.GetProperty("nullable").GetBoolean());
            Assert.Equal(new[] { "true", "false" }, maybe.GetProperty("options").EnumerateArray().Select(x => x.GetString()));
            Assert.True(day.GetProperty("nullable").GetBoolean());
            Assert.False(day.GetProperty("multiple").GetBoolean());
            Assert.True(flags.GetProperty("multiple").GetBoolean());
            Assert.Contains("Public", flags.GetProperty("options").EnumerateArray().Select(x => x.GetString()));
        }

        [Fact]
        public void CombineFormValues_TakesTrue_WhenACheckboxSendsItAfterItsHiddenFalse()
        {
            var parameter = FormEntry(x => x.Run(true, null, null, BindingFlags.Default)).Parameters[0];

            Assert.Equal("false", JobCatalog.CombineFormValues(parameter, new[] { "false" }));
            Assert.Equal("true", JobCatalog.CombineFormValues(parameter, new[] { "false", "true" }));
            Assert.Null(JobCatalog.CombineFormValues(parameter, new string[0]));
            Assert.Equal(false, JobCatalog.ConvertArgument(parameter, "false"));
        }

        [Fact]
        public void CombineFormValues_CombinesTheChoicesOfAFlagsEnum()
        {
            var parameter = FormEntry(x => x.Run(true, null, null, BindingFlags.Default)).Parameters[3];

            Assert.Equal("Instance, Public", JobCatalog.CombineFormValues(parameter, new[] { "0", "Instance", "Public" }));
            Assert.Equal("0", JobCatalog.CombineFormValues(parameter, new[] { "0" }));
            Assert.Equal(BindingFlags.Instance | BindingFlags.Public, JobCatalog.ConvertArgument(parameter, "Instance, Public"));
            Assert.Equal(BindingFlags.Default, JobCatalog.ConvertArgument(parameter, "0"));
        }

        [Fact]
        public void RunAgain_GivesBackFalseNullAndCombinedFlags_WhenTheFormIsSentAsItIs()
        {
            // The form pre-filled from the job: a false bool whose default is true, null bool? and enum, two flags
            var job = Job.FromExpression<CatalogFormJobs>(x => x.Run(false, null, null, BindingFlags.Instance | BindingFlags.Public));
            var entry = JobCatalog.CreateEntry(job);
            var texts = JobCatalog.FormatArguments(job);

            Assert.Equal(new[] { "false", "", "", "Instance, Public" }, texts);
            Assert.Equal(false, JobCatalog.ConvertArgument(entry.Parameters[0], "false"));
            // The form selects "(null)" for an empty text of a nullable select
            Assert.Null(JobCatalog.ConvertArgument(entry.Parameters[1], "null"));
            Assert.Null(JobCatalog.ConvertArgument(entry.Parameters[2], "null"));
            Assert.Equal(BindingFlags.Instance | BindingFlags.Public, JobCatalog.ConvertArgument(entry.Parameters[3], JobCatalog.CombineFormValues(entry.Parameters[3], new[] { "0", "Instance", "Public" })));
        }

        private static JobCatalogEntry FormEntry(Expression<Action<CatalogFormJobs>> call)
        {
            return JobCatalog.CreateEntry(Job.FromExpression(call));
        }

        public class CatalogFormJobs
        {
            public void Run(bool dryRun = true, bool? enabled = true, DayOfWeek? day = DayOfWeek.Monday, BindingFlags flags = BindingFlags.Instance) { }
        }

        private static string TypeNameOf(Type type) => $"{type.FullName}, {type.Assembly.GetName().Name}";

        private static JobCatalogEntry InputEntry(string method)
        {
            var info = typeof(CatalogInputJobs).GetMethod(method);
            return JobCatalog.CreateEntry(new Job(typeof(CatalogInputJobs), info, new object[info.GetParameters().Length]));
        }

        public class CatalogPayload
        {
            public string Name { get; set; }
        }

        public class CatalogWrapper
        {
            public object Data { get; set; }
        }

        public interface ICatalogMessage
        {
            string Text { get; }
        }

        public class CatalogMessage : ICatalogMessage
        {
            public string Text { get; set; }
        }

        public class CatalogInputJobs
        {
            public void Handle(object payload) { }

            public void Wrap(CatalogWrapper wrapper) { }

            public void Send(ICatalogMessage message) { }

            public void Mixed(object payload, ICatalogMessage message) { }

            public void Load(Type type) { }

            public void LoadWrapped(CatalogTypeWrapper wrapper) { }
        }

        public class CatalogTypeWrapper
        {
            public Type Kind { get; set; }
        }

        public record CatalogRecord
        {
            public void Build(int id) { }
        }

        public class CatalogJobsWithOverrides
        {
            public void Run() { }

            public override string ToString() => "jobs";

            public override bool Equals(object obj) => ReferenceEquals(this, obj);

            public override int GetHashCode() => 1;
        }

        public class CatalogDateJobs
        {
            public void At(DateTimeOffset when, DateTimeOffset? maybe) { }

            public void On(DateTime when, DateTime? maybe) { }
        }

        public class CatalogJobs
        {
            public string Property { get; set; }

            [AutomaticRetry(Attempts = 2), JobTimeout(30)]
            public Task Import(string file, int rows = 10, IJobConsole console = null, CancellationToken token = default) => Task.CompletedTask;

            public void Notify(Channel channel, bool urgent, DateTime? when, double ratio, string[] tags) { }

            public void Lookup(Guid id, TimeSpan delay, char separator, Guid? otherId, TimeSpan? timeout) { }

            public static void StaticOne() { }

            public void Generic<T>(T value) { }

            public void WithOut(out int value) { value = 0; }
        }
    }
}
