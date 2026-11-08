#nullable enable

using System.Threading;

namespace Masterly.NightShift
{
    /// <summary>
    /// The time zone resolver used where none is passed explicitly: by the static <see cref="RecurringJob"/>
    /// class, by recurring job managers and schedulers created without one, and by servers whose
    /// <see cref="BackgroundJobServerOptions.TimeZoneResolver"/> is <c>null</c>. Set it with
    /// <see cref="GlobalConfigurationExtensions.UseTimeZoneResolver"/>.
    /// </summary>
    internal static class GlobalTimeZoneResolver
    {
        private static readonly ITimeZoneResolver Default = new DefaultTimeZoneResolver();
        private static ITimeZoneResolver? _current;

        /// <summary>The configured resolver, or a <see cref="DefaultTimeZoneResolver"/>.</summary>
        public static ITimeZoneResolver Current => Volatile.Read(ref _current) ?? Default;

        /// <summary>Sets the resolver; <c>null</c> restores the <see cref="DefaultTimeZoneResolver"/>.</summary>
        public static void Set(ITimeZoneResolver? timeZoneResolver)
        {
            Volatile.Write(ref _current, timeZoneResolver);
        }
    }
}
