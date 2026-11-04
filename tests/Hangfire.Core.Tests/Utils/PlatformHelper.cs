using System;

namespace Hangfire.Core.Tests
{
    internal static class PlatformHelper
    {
        public static bool IsRunningOnWindows()
        {
            return Environment.OSVersion.Platform == PlatformID.Win32NT;
        }
    }
}
