using System.Linq;
using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Common;
using Masterly.NightShift.Dashboard;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    public class RedactedAttributeFacts
    {
        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Connect(string host, [Redacted] string password, int port) { }

        [Fact]
        public void IsRedacted_RecognizesTheAttribute()
        {
            var parameters = typeof(RedactedAttributeFacts).GetMethod(nameof(Connect)).GetParameters();

            Assert.False(RedactedAttribute.IsRedacted(parameters[0]));
            Assert.True(RedactedAttribute.IsRedacted(parameters[1]));
            Assert.False(RedactedAttribute.IsRedacted(parameters[2]));
        }

        [Fact]
        public void JobMethodCallRenderer_MasksRedactedArguments()
        {
            var job = Job.FromExpression(() => Connect("db.local", "hunter2", 1433));

            var html = JobMethodCallRenderer.Render(job).ToString();

            Assert.DoesNotContain("hunter2", html);
            Assert.Contains(RedactedAttribute.Mask, html);
            Assert.Contains("hf-redacted", html);
            Assert.Contains("db.local", html);
            Assert.Contains("1433", html);
        }

        [Fact]
        public void JobCodeRenderer_LeavesRedactedArgumentsOut()
        {
            var job = Job.FromExpression(() => Connect("db.local", "hunter2", 1433));

            var code = JobCodeRenderer.Render(job);

            Assert.DoesNotContain("hunter2", code);
            Assert.Contains("/* password: redacted */ \"\"", code);
            Assert.Contains("\"db.local\"", code);
        }

        [Fact]
        public void JobCatalog_FormatArguments_SkipsRedactedParameters()
        {
            var job = Job.FromExpression(() => Connect("db.local", "hunter2", 1433));

            var arguments = JobCatalog.FormatArguments(job);

            Assert.Equal(new[] { "db.local", null, "1433" }, arguments.ToArray());
        }
    }
}
