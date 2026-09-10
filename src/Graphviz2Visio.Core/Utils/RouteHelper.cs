using System;
using System.Collections.Generic;
using Graphviz2Visio.Core.Models;

namespace Graphviz2Visio.Core.Utils
{
    public static class RouteHelper
    {
        public static List<Pt> CreateRoute(EdgeInfo edge, NodeInfo sourceNode, NodeInfo targetNode)
        {
            var route = new List<Pt>();
            IList<Pt> routePoints = IsOrthogonalPointChain(edge.Points)
                ? edge.Points
                : BezierHelper.SplineToPolyline(edge.Points, Math.Max(6, edge.Points.Count));

            foreach (Pt point in routePoints)
                AddRoutePoint(route, point);

            if (route.Count >= 2)
            {
                route[0] = AttachToNodeBoundary(sourceNode, route[0], route[1]);
                route[route.Count - 1] = AttachToNodeBoundary(
                    targetNode,
                    route[route.Count - 1],
                    route[route.Count - 2]);
                return route;
            }

            if (sourceNode == null || targetNode == null)
                return route;

            Pt sourcePort = AttachToNodeBoundary(
                sourceNode,
                new Pt(sourceNode.Cx, sourceNode.Cy),
                new Pt(targetNode.Cx, targetNode.Cy));
            Pt targetPort = AttachToNodeBoundary(
                targetNode,
                new Pt(targetNode.Cx, targetNode.Cy),
                new Pt(sourceNode.Cx, sourceNode.Cy));
            AddRoutePoint(route, sourcePort);
            AddRoutePoint(route, targetPort);
            return route;
        }

        private static bool IsOrthogonalPointChain(IList<Pt> points)
        {
            if (points == null || points.Count < 2)
                return false;

            for (int index = 0; index < points.Count - 1; index++)
            {
                bool horizontal = Math.Abs(points[index].Y - points[index + 1].Y) < 0.001;
                bool vertical = Math.Abs(points[index].X - points[index + 1].X) < 0.001;
                if (!horizontal && !vertical)
                    return false;
            }

            return true;
        }

        private static void AddRoutePoint(ICollection<Pt> route, Pt point)
        {
            bool hasLastPoint = false;
            Pt lastPoint = default(Pt);
            foreach (Pt existingPoint in route)
            {
                lastPoint = existingPoint;
                hasLastPoint = true;
            }

            if (!hasLastPoint || Math.Abs(lastPoint.X - point.X) > 0.001 ||
                Math.Abs(lastPoint.Y - point.Y) > 0.001)
            {
                route.Add(point);
            }
        }

        private static Pt AttachToNodeBoundary(NodeInfo node, Pt fallbackPoint, Pt towardPoint)
        {
            if (node == null)
                return fallbackPoint;

            double dx = towardPoint.X - node.Cx;
            double dy = towardPoint.Y - node.Cy;
            if (Math.Abs(dx) < 0.000001 && Math.Abs(dy) < 0.000001)
            {
                dx = fallbackPoint.X - node.Cx;
                dy = fallbackPoint.Y - node.Cy;
            }

            double scale = GetBoundaryScale(node, dx, dy);
            if (scale <= 0)
                return fallbackPoint;

            return new Pt(node.Cx + dx * scale, node.Cy + dy * scale);
        }

        private static double GetBoundaryScale(NodeInfo node, double dx, double dy)
        {
            double hw = Math.Max(node.W / 2.0, 0.000001);
            double hh = Math.Max(node.H / 2.0, 0.000001);
            string shapeType = (node.Shape ?? "box").ToLowerInvariant();

            if (Math.Abs(dx) < 0.000001 && Math.Abs(dy) < 0.000001)
                return 0;

            if (shapeType == "ellipse")
                return 1.0 / Math.Sqrt(dx * dx / (hw * hw) + dy * dy / (hh * hh));

            if (shapeType == "diamond")
                return 1.0 / (Math.Abs(dx) / hw + Math.Abs(dy) / hh);

            double scaleX = Math.Abs(dx) < 0.000001 ? double.PositiveInfinity : hw / Math.Abs(dx);
            double scaleY = Math.Abs(dy) < 0.000001 ? double.PositiveInfinity : hh / Math.Abs(dy);
            return Math.Min(scaleX, scaleY);
        }
    }
}
