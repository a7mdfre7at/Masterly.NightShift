using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Threading;
using Masterly.NightShift.Common;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Server;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard
{
    public class JobCodeRendererFacts
    {
        public enum Level { Low, High }

        [Fact]
        public void Render_HandlesAMissingJob()
        {
            Assert.StartsWith("//", JobCodeRenderer.Render(null));
        }

        [Fact]
        public void Render_WritesAStaticCall_WithLiterals()
        {
            var job = Job.FromExpression(() => Static("a \"quoted\" value", 42, true));

            Assert.Equal("BackgroundJob.Enqueue(() => JobCodeRendererFacts.Static(\"a \\\"quoted\\\" value\", 42, true));", JobCodeRenderer.Render(job));
        }

        [Fact]
        public void Render_WritesAnInstanceCall_SkippingInjectedParameters()
        {
            var job = Job.FromExpression<JobCodeRendererFacts>(x => x.Instance(Level.High, 2.5, null, CancellationToken.None));

            Assert.Equal("BackgroundJob.Enqueue<JobCodeRendererFacts>(x => x.Instance(Level.High, 2.5, null, CancellationToken.None));", JobCodeRenderer.Render(job));
        }

        [Fact]
        public void Render_FallsBackToFromJson_ForComplexArguments()
        {
            var job = Job.FromExpression(() => Complex(new List<int> { 1, 2 }, new DateTime(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc)));

            var code = JobCodeRenderer.Render(job);

            Assert.Contains("FromJson<List<int>>(\"[1,2]\")", code);
            Assert.Contains("DateTime.Parse(\"2026-11-04T00:00:00.0000000Z\")", code);
        }

        [Fact]
        public void Render_SplitsLongArgumentLists()
        {
            var job = Job.FromExpression(() => Static(new string('x', 90), 1, false));

            var code = JobCodeRenderer.Render(job);

            Assert.Contains(Environment.NewLine + "    \"xxx", code);
        }

        [Theory]
        [InlineData(5L, "5L")]
        [InlineData(1.5f, "1.5f")]
        [InlineData(3.0, "3.0")]
        [InlineData(2.25, "2.25")]
        [InlineData('c', "'c'")]
        [InlineData('\'', "'\\''")]
        [InlineData(7U, "7U")]
        [InlineData(7UL, "7UL")]
        [InlineData((short)-3, "-3")]
        [InlineData((byte)255, "255")]
        [InlineData(false, "false")]
        public void Literal_FormatsNumbersAndChars(object value, string expected)
        {
            Assert.Equal(expected, JobCodeRenderer.Literal(value, value.GetType()));
        }

        [Fact]
        public void Literal_FormatsDateTimeOffsets_WithTheirOffset()
        {
            var value = new DateTimeOffset(2026, 11, 4, 1, 2, 3, TimeSpan.FromHours(2));

            Assert.Equal("DateTimeOffset.Parse(\"2026-11-04T01:02:03.0000000+02:00\")", JobCodeRenderer.Literal(value, typeof(DateTimeOffset)));
        }

        [Fact]
        public void Literal_NamesTheNullableEnumType()
        {
            Assert.Equal("Level.High", JobCodeRenderer.Literal(Level.High, typeof(Level?)));
        }

        [Fact]
        public void Literal_SkipsValuesTooBigToInline()
        {
            var value = new[] { new string('x', 2100) };

            Assert.Equal("/* string[]: value too big to inline */ null", JobCodeRenderer.Literal(value, typeof(string[])));
        }

        [Fact]
        public void Literal_FallsBackToToString_WhenTheValueCannotBeSerialized()
        {
            Assert.Equal("FromJson<Unserializable>(\"unserializable \\\"value\\\"\")", JobCodeRenderer.Literal(new Unserializable(), typeof(Unserializable)));
        }

        [Fact]
        public void Literal_ThrowsWhenTypeIsNull()
        {
            Assert.Throws<ArgumentNullException>(() => JobCodeRenderer.Literal(1, null));
        }

        [Fact]
        public void Render_WritesGenericMethodArguments()
        {
            var job = Job.FromExpression(() => Generic<int?, Dictionary<string, long>>(null, null));

            Assert.Equal("BackgroundJob.Enqueue(() => JobCodeRendererFacts.Generic<int?, Dictionary<string, long>>(null, null));", JobCodeRenderer.Render(job));
        }

        [Fact]
        public void TypeName_UsesCSharpKeywords_AndArrays()
        {
            Assert.Equal("object[]", JobCodeRenderer.TypeName(typeof(object[])));
            Assert.Equal("bool?", JobCodeRenderer.TypeName(typeof(bool?)));
            Assert.Equal("decimal", JobCodeRenderer.TypeName(typeof(decimal)));
            Assert.Equal("double", JobCodeRenderer.TypeName(typeof(double)));
            Assert.Equal("Guid", JobCodeRenderer.TypeName(typeof(Guid)));
        }

        [Fact]
        public void Quote_EscapesBackslashes()
        {
            Assert.Equal("\"C:\\\\temp\"", JobCodeRenderer.Quote("C:\\temp"));
            Assert.Equal("@\"say \"\"hi\"\"\r\n\"", JobCodeRenderer.Quote("say \"hi\"\r\n"));
        }

        public sealed class Unserializable
        {
            public int Value => throw new InvalidOperationException("cannot read");

            public override string ToString() => "unserializable \"value\"";
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Generic<T1, T2>(T1 first, T2 second) { }

        [Fact]
        public void Literal_FormatsGuidsTimeSpansDecimalsAndMultilineStrings()
        {
            Assert.Equal("Guid.Parse(\"00000000-0000-0000-0000-000000000001\")", JobCodeRenderer.Literal(new Guid("00000000-0000-0000-0000-000000000001"), typeof(Guid)));
            Assert.Equal("TimeSpan.Parse(\"00:05:00\")", JobCodeRenderer.Literal(TimeSpan.FromMinutes(5), typeof(TimeSpan)));
            Assert.Equal("1.25m", JobCodeRenderer.Literal(1.25m, typeof(decimal)));
            Assert.Equal("@\"a\nb\"", JobCodeRenderer.Literal("a\nb", typeof(string)));
            Assert.Equal("null", JobCodeRenderer.Literal(null, typeof(string)));
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Static(string text, int number, bool flag) { }
        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Complex(List<int> values, DateTime at) { }
        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public void Instance(Level level, double ratio, IJobConsole console, CancellationToken token) { }
    }
}
