using System.Linq;
using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Common;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Server;
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

        [Fact]
        public void JobDisplayName_ShowsTheMask_ForARedactedArgument()
        {
            var job = Job.FromExpression(() => Connect("db.local", "hunter2", 1433));

            var name = new JobDisplayNameAttribute("Connect to {0} with {1} on port {2}").Format(null, job);

            Assert.Equal($"Connect to db.local with {RedactedAttribute.Mask} on port 1433", name);
        }

        [Fact]
        public void Hash_IsStable_Short_AndDiffersBetweenValues()
        {
            var hash = RedactedAttribute.Hash("hunter2");

            Assert.Equal(hash, RedactedAttribute.Hash("hunter2"));
            Assert.NotEqual(hash, RedactedAttribute.Hash("hunter3"));
            Assert.Matches("^[0-9a-f]{16}$", hash);
        }

        [Fact]
        public void ResourceTemplates_UseAHash_ForARedactedArgument()
        {
            var job = Job.FromExpression(() => Connect("DB.local", "hunter2", 1433));
            var other = Job.FromExpression(() => Connect("DB.local", "letmein", 1433));

            var resource = ConcurrencyControl.FormatResource("connect-{0}-{1}-{2}", job);

            Assert.Equal($"connect-db.local-{RedactedAttribute.Hash("hunter2")}-1433", resource);
            Assert.NotEqual(resource, ConcurrencyControl.FormatResource("connect-{0}-{1}-{2}", other));
        }

        [Fact]
        public void PreventDuplicates_Template_UsesAHash_ForARedactedArgument()
        {
            var job = Job.FromExpression(() => Connect("db.local", "hunter2", 1433));

            var fingerprint = new PreventDuplicatesAttribute("connect:{1}").GetFingerprint(job);

            Assert.Equal($"connect:{RedactedAttribute.Hash("hunter2")}", fingerprint);
        }
    }
}
