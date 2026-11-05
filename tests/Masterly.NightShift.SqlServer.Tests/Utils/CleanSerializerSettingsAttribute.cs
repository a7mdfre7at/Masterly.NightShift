using System.Reflection;
using Masterly.NightShift.Common;
using Xunit.v3;

namespace Masterly.NightShift.SqlServer.Tests
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
            GlobalConfiguration.Configuration.UseSerializerSettings(null);
        }
    }
}
