using System.Globalization;
using System.Text;

namespace Masterly.NightShift.Dashboard;

/// <summary>
/// Renders a series of values as a small inline SVG line, used by the Insights page for duration trends.
/// </summary>
internal static class Sparkline
{
    public static NonEscapedString Render(long[] values, int width, int height, bool highlightLast)
    {
        if (values == null || values.Length == 0)
        {
            return new NonEscapedString(string.Empty);
        }

        long max = Math.Max(1, values.Max());
        int count = values.Length;
        double step = count > 1 ? (double)(width - 2) / (count - 1) : 0;

        StringBuilder points = new();
        double lastX = 1, lastY = height - 1;

        for (int i = 0; i < count; i++)
        {
            lastX = 1 + i * step;
            lastY = height - 1 - (double)values[i] / max * (height - 2);

            if (i > 0)
            {
                points.Append(' ');
            }

            points.Append(lastX.ToString("0.#", CultureInfo.InvariantCulture)).Append(',').Append(lastY.ToString("0.#", CultureInfo.InvariantCulture));
        }

        StringBuilder svg = new();
        svg.Append("<svg class=\"sparkline\" width=\"").Append(width).Append("\" height=\"").Append(height)
            .Append("\" viewBox=\"0 0 ").Append(width).Append(' ').Append(height).Append("\" aria-hidden=\"true\">");
        svg.Append("<polyline fill=\"none\" stroke-width=\"1.5\" points=\"").Append(points).Append("\"/>");
        svg.Append("<circle class=\"sparkline-last").Append(highlightLast ? " sparkline-last-failed" : string.Empty)
            .Append("\" r=\"2.5\" cx=\"").Append(lastX.ToString("0.#", CultureInfo.InvariantCulture))
            .Append("\" cy=\"").Append(lastY.ToString("0.#", CultureInfo.InvariantCulture)).Append("\"/>");
        svg.Append("</svg>");

        return new NonEscapedString(svg.ToString());
    }
}
