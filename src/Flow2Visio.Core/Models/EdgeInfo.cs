using System.Collections.Generic;

namespace Flow2Visio.Core.Models
{
    public class EdgeInfo
    {
        public string From { get; set; }
        public string To { get; set; }

        public List<Pt> Points { get; set; } = new List<Pt>();

        public string Label { get; set; }
        public double LabelX { get; set; }
        public double LabelY { get; set; }

        public string Style { get; set; } = "solid";
        public string Color { get; set; } = "black";
    }
}
