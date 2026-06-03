using System.Globalization;

namespace Graphviz2Visio.Core.Utils
{
    public static class ColorHelper
    {
        public static string ToVisioColorFormula(string name, string fallback = "black")
        {
            string c = Normalize(name);
            if (string.IsNullOrWhiteSpace(c))
                c = Normalize(fallback);

            switch (c)
            {
                case "black": return "RGB(0,0,0)";
                case "white": return "RGB(255,255,255)";
                case "lightgrey":
                case "lightgray": return "RGB(230,230,230)";
                case "grey":
                case "gray": return "RGB(128,128,128)";
                case "red": return "RGB(255,0,0)";
                case "green": return "RGB(0,128,0)";
                case "blue": return "RGB(0,0,255)";
                case "yellow": return "RGB(255,255,0)";
                case "orange": return "RGB(255,165,0)";
            }

            if (c.StartsWith("#") && c.Length == 7)
            {
                int r = int.Parse(c.Substring(1, 2), NumberStyles.HexNumber);
                int g = int.Parse(c.Substring(3, 2), NumberStyles.HexNumber);
                int b = int.Parse(c.Substring(5, 2), NumberStyles.HexNumber);
                return string.Format(CultureInfo.InvariantCulture, "RGB({0},{1},{2})", r, g, b);
            }

            return "RGB(0,0,0)";
        }

        private static string Normalize(string s)
        {
            return (s ?? string.Empty).Trim().ToLowerInvariant();
        }
    }
}
