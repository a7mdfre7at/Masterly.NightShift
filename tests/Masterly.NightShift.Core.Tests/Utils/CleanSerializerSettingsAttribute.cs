using System.Reflection;
using Masterly.NightShift.Common;
using Xunit.v3;

namespace Masterly.NightShift.Core.Tests
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
            SerializationHelper.SetUserSerializerSettings(null);
        }
    }
}
