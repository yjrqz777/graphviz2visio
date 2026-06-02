using System.Collections.Generic;

namespace Flow2Visio.Core.Models
{
    public class GraphInfo
    {
        public double Scale { get; set; } = 1.0;
        public double Width { get; set; }
        public double Height { get; set; }

        public List<NodeInfo> Nodes { get; set; } = new List<NodeInfo>();
        public List<EdgeInfo> Edges { get; set; } = new List<EdgeInfo>();
    }
}
