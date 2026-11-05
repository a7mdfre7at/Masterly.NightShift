// This file is part of Hangfire. Copyright © 2026 Hangfire OÜ.
//
// Hangfire is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as
// published by the Free Software Foundation, either version 3
// of the License, or any later version.
//
// Hangfire is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
//
// You should have received a copy of the GNU Lesser General Public
// License along with Hangfire. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Masterly.NightShift.Dashboard
{
    /// <summary>
    /// Renders a series of values as a small inline SVG line, used by the Insights page for duration trends.
    /// </summary>
    internal static class Sparkline
    {
        public static NonEscapedString Render(long[] values, int width, int height, bool highlightLast)
        {
            if (values == null || values.Length == 0)
            {
                return new NonEscapedString(String.Empty);
            }

            var max = Math.Max(1, values.Max());
            var count = values.Length;
            var step = count > 1 ? (double)(width - 2) / (count - 1) : 0;

            var points = new StringBuilder();
            double lastX = 1, lastY = height - 1;

            for (var i = 0; i < count; i++)
            {
                lastX = 1 + i * step;
                lastY = height - 1 - (double)values[i] / max * (height - 2);

                if (i > 0) points.Append(' ');
                points.Append(lastX.ToString("0.#", CultureInfo.InvariantCulture)).Append(',').Append(lastY.ToString("0.#", CultureInfo.InvariantCulture));
            }

            var svg = new StringBuilder();
            svg.Append("<svg class=\"sparkline\" width=\"").Append(width).Append("\" height=\"").Append(height)
                .Append("\" viewBox=\"0 0 ").Append(width).Append(' ').Append(height).Append("\" aria-hidden=\"true\">");
            svg.Append("<polyline fill=\"none\" stroke-width=\"1.5\" points=\"").Append(points).Append("\"/>");
            svg.Append("<circle class=\"sparkline-last").Append(highlightLast ? " sparkline-last-failed" : String.Empty)
                .Append("\" r=\"2.5\" cx=\"").Append(lastX.ToString("0.#", CultureInfo.InvariantCulture))
                .Append("\" cy=\"").Append(lastY.ToString("0.#", CultureInfo.InvariantCulture)).Append("\"/>");
            svg.Append("</svg>");

            return new NonEscapedString(svg.ToString());
        }
    }
}
