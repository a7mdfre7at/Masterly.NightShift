using System.Text;

namespace Masterly.NightShift.Dashboard;

/// <summary>Writes and reads the values that dashboard routes carry in the path: job ids, queues, tags and so on.</summary>
internal static class RouteValues
{
    /// <summary>The character that escapes <c>%</c>, <c>/</c> and itself inside a value of the path.</summary>
    private const char Escape = '~';

    /// <summary>
    /// The value as one segment of a path. ASP.NET Core decodes the request path before the dashboard sees
    /// it, except <c>%2F</c>, which it keeps so that a <c>/</c> inside a value doesn't look like a separator.
    /// So <c>encodeURIComponent</c> alone gives the same path for <c>a/b</c> and for the literal <c>a%2Fb</c>.
    /// <c>%</c>, <c>/</c> and <c>~</c> are therefore written <c>~25</c>, <c>~2F</c> and <c>~7E</c> first: the
    /// segment then holds no <c>%</c> that stands for itself, and no host decodes <c>~</c>. The scripts do the
    /// same (<c>NightShift.routeValue</c> in <c>nightshift.js</c>).
    /// </summary>
    public static string Encode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        string escaped = value
            .Replace("~", "~7E", StringComparison.Ordinal)
            .Replace("%", "~25", StringComparison.Ordinal)
            .Replace("/", "~2F", StringComparison.Ordinal);

        return Uri.EscapeDataString(escaped);
    }

    /// <summary>
    /// Reads a value written by <see cref="Encode"/> from the path ASP.NET Core decoded. A <c>%2F</c> is read
    /// as <c>/</c> too, so links that encode a value with <c>encodeURIComponent</c> alone, as earlier versions
    /// and extensions do, keep working. Nothing else is decoded a second time: that would turn a literal
    /// <c>%41</c> into <c>A</c>. A <c>~</c> not followed by <c>25</c>, <c>2F</c> or <c>7E</c> stands for itself.
    /// </summary>
    public static string Decode(string value)
    {
        if (string.IsNullOrEmpty(value) || (value.IndexOf('%', StringComparison.Ordinal) < 0 && value.IndexOf(Escape, StringComparison.Ordinal) < 0))
        {
            return value;
        }

        StringBuilder decoded = new(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char current = value[i];
            char? escaped = i + 2 < value.Length ? Unescape(current, value[i + 1], value[i + 2]) : null;
            if (escaped.HasValue)
            {
                decoded.Append(escaped.Value);
                i += 2;
            }
            else
            {
                decoded.Append(current);
            }
        }

        return decoded.ToString();
    }

    /// <summary>The decoded value of a named group of the current route.</summary>
    public static string Get(DashboardContext context, string groupName)
    {
        return Decode(context.UriMatch.Groups[groupName].Value);
    }

    private static char? Unescape(char marker, char high, char low)
    {
        if (marker == '%')
        {
            return high == '2' && low is 'F' or 'f' ? '/' : null;
        }

        if (marker != Escape)
        {
            return null;
        }

        return (high, low) switch
        {
            ('2', '5') => '%',
            ('2', 'F' or 'f') => '/',
            ('7', 'E' or 'e') => Escape,
            _ => null,
        };
    }
}
