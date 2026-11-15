using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using Masterly.NightShift.Dashboard.Resources;

namespace Masterly.NightShift.Dashboard;

/// <summary>The language a dashboard page's text is in, for the <c>lang</c> attribute of its <c>html</c> element.</summary>
internal static class DocumentLanguage
{
    /// <summary>The language of the strings that have no satellite assembly (<c>NeutralResourcesLanguage</c>).</summary>
    internal const string Neutral = "en";

    private static readonly ConcurrentDictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The most specific of <paramref name="culture"/> and its parents that the dashboard has strings for, as a
    /// BCP 47 tag (<c>de</c>, <c>pt-BR</c>). The invariant culture, which .NET names <c>iv</c>, and a language
    /// without a translation get <see cref="Neutral"/>, because their pages show the English strings.
    /// </summary>
    public static string Of(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return Languages.GetOrAdd(culture.Name, static (_, c) => Find(c), culture);
    }

    // Asks for the satellite assembly itself: ResourceManager.GetResourceSet can't tell, because a lookup that fell
    // back to the English strings caches them under the culture it was made for
    private static string Find(CultureInfo culture)
    {
        Assembly assembly = typeof(Strings).Assembly;
        for (CultureInfo current = culture; !string.IsNullOrEmpty(current.Name); current = current.Parent)
        {
            if (HasSatellite(assembly, current))
            {
                return current.Name;
            }
        }

        return Neutral;
    }

    private static bool HasSatellite(Assembly assembly, CultureInfo culture)
    {
        try
        {
            return assembly.GetSatelliteAssembly(culture) != null;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }
}
