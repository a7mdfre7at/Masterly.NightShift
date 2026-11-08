using System;

namespace Masterly.NightShift.Dashboard
{
    /// <summary>Reads the values that dashboard routes capture from the path: job ids, queues, tags and so on.</summary>
    internal static class RouteValues
    {
        /// <summary>
        /// ASP.NET Core decodes the request path before the dashboard sees it, except <c>%2F</c>, which it keeps
        /// so that a <c>/</c> inside a value doesn't look like a separator. The dashboard's scripts encode values
        /// with <c>encodeURIComponent</c>, so an id with a <c>/</c> arrives as <c>%2F</c>. Only that sequence is
        /// decoded here: decoding everything a second time would turn a literal <c>%41</c> in an id into <c>A</c>.
        /// </summary>
        public static string Decode(string value)
        {
            if (String.IsNullOrEmpty(value) || value.IndexOf('%') < 0) return value;

            return value.Replace("%2F", "/").Replace("%2f", "/");
        }

        /// <summary>The decoded value of a named group of the current route.</summary>
        public static string Get(DashboardContext context, string groupName)
        {
            return Decode(context.UriMatch.Groups[groupName].Value);
        }
    }
}
