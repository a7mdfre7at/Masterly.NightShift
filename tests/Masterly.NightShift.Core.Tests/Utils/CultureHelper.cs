using System.Globalization;
using System.Threading;

namespace Masterly.NightShift.Core.Tests
{
    internal static class CultureHelper
    {
        public static void SetCurrentCulture(CultureInfo cultureInfo)
        {
            Thread.CurrentThread.CurrentCulture = cultureInfo;
        }

        public static void SetCurrentUICulture(CultureInfo cultureInfo)
        {
            Thread.CurrentThread.CurrentUICulture = cultureInfo;
        }

        public static void SetCurrentCulture(string id)
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo(id);
        }

        public static void SetCurrentUICulture(string id)
        {
            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(id);
        }
    }
}
