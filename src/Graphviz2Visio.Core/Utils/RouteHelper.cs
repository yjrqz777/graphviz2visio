using System;
using System.Collections.Generic;
using Graphviz2Visio.Core.Models;

namespace Graphviz2Visio.Core.Utils
{
    public static class RouteHelper
    {
        public static Dictionary<EdgeInfo, List<Pt>> CreateRoutes(GraphInfo graph)
        {
            return OrthogonalRouter.Route(graph);
        }

        public static List<Pt> CreateRoute(EdgeInfo edge, NodeInfo sourceNode, NodeInfo targetNode)
        {
            if (edge == null)
                throw new ArgumentNullException(nameof(edge));

            var graph = new GraphInfo();
            if (sourceNode != null)
                graph.Nodes.Add(sourceNode);
            if (targetNode != null && !ReferenceEquals(sourceNode, targetNode))
                graph.Nodes.Add(targetNode);
            graph.Edges.Add(edge);

            Dictionary<EdgeInfo, List<Pt>> routes = CreateRoutes(graph);
            List<Pt> route;
            return routes.TryGetValue(edge, out route) ? route : new List<Pt>();
        }

        public static bool IsOrthogonalPointChain(IList<Pt> points)
        {
            if (points == null || points.Count < 2)
                return false;

            for (int index = 0; index + 1 < points.Count; index++)
            {
                bool horizontal = Math.Abs(points[index].Y - points[index + 1].Y) < 0.001;
                bool vertical = Math.Abs(points[index].X - points[index + 1].X) < 0.001;
                if (!horizontal && !vertical)
                    return false;
            }
            return true;
        }
    }
}
