using System.Reflection;
using Hangfire.Common;
using Newtonsoft.Json;
using Xunit.v3;

namespace Hangfire.SqlServer.Tests
{
    internal sealed class CleanSerializerSettingsAttribute : BeforeAfterTestAttribute
    {
        public override void Before(MethodInfo methodUnderTest, IXunitTest test)
        {
            ClearSettings();
        }

        public override void After(MethodInfo methodUnderTest, IXunitTest test)
        {
            ClearSettings();
        }

        private static void ClearSettings()
        {
#pragma warning disable 618
            JobHelper.SetSerializerSettings(null);
#pragma warning restore 618
            GlobalConfiguration.Configuration.UseSerializerSettings(null);
            JsonConvert.DefaultSettings = null;
        }
    }
}
