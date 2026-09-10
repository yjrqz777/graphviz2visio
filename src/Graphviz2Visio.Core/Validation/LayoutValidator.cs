using System;
using System.Collections.Generic;
using Graphviz2Visio.Core.Models;
using Graphviz2Visio.Core.Utils;

namespace Graphviz2Visio.Core.Validation
{
    public static class LayoutValidator
    {
        private const double Epsilon = 0.001;
        private const int MaxIssues = 200;

        public static ValidationResult Validate(GraphInfo graph)
        {
            if (graph == null)
                throw new ArgumentNullException(nameof(graph));

            return Validate(graph, RouteHelper.CreateRoutes(graph));
        }

        public static ValidationResult ValidateRaw(GraphInfo graph)
        {
            if (graph == null)
                throw new ArgumentNullException(nameof(graph));

            var routes = new Dictionary<EdgeInfo, List<Pt>>();
            foreach (EdgeInfo edge in graph.Edges)
                routes[edge] = edge.Points == null ? new List<Pt>() : new List<Pt>(edge.Points);
            return ValidateCore(graph, routes, "layout-detected");
        }

        public static ValidationResult Validate(
            GraphInfo graph,
            IDictionary<EdgeInfo, List<Pt>> routes)
        {
            if (graph == null)
                throw new ArgumentNullException(nameof(graph));
            if (routes == null)
                throw new ArgumentNullException(nameof(routes));

            return ValidateCore(graph, routes, "layout");
        }

        private static ValidationResult ValidateCore(
            GraphInfo graph,
            IDictionary<EdgeInfo, List<Pt>> suppliedRoutes,
            string layer)
        {
            var result = new ValidationResult { Layer = layer };
            var routes = new Dictionary<EdgeInfo, List<Pt>>();
            foreach (EdgeInfo edge in graph.Edges)
            {
                List<Pt> route;
                if (!suppliedRoutes.TryGetValue(edge, out route) || route == null)
                    route = new List<Pt>();
                routes[edge] = route;

                if (route.Count < 2)
                {
                    result.Add(
                        "layout.route.missing",
                        "连线没有足够的路径点。",
                        edge: EdgeName(edge));
                    continue;
                }

                if (!RouteHelper.IsOrthogonalPointChain(route))
                {
                    result.Add(
                        "layout.non-orthogonal-segment",
                        "连线包含斜线段；最终路径只允许水平或垂直线段。",
                        edge: EdgeName(edge));
                }
            }

            ValidateNodeIntersections(graph, routes, result);
            ValidateEdgeIntersections(graph.Edges, routes, result);
            ValidateLongSegments(graph, routes, result);
            ValidateLabelPositions(graph, result);
            return result;
        }

        private static void ValidateNodeIntersections(
            GraphInfo graph,
            IDictionary<EdgeInfo, List<Pt>> routes,
            ValidationResult result)
        {
            foreach (EdgeInfo edge in graph.Edges)
            {
                List<Pt> route = routes[edge];
                for (int segmentIndex = 0; segmentIndex + 1 < route.Count; segmentIndex++)
                {
                    foreach (NodeInfo node in graph.Nodes)
                    {
                        if (node.Id == edge.From && segmentIndex == 0)
                            continue;
                        if (node.Id == edge.To && segmentIndex == route.Count - 2)
                            continue;

                        Pt hit;
                        if (SegmentIntersectsRectangleInterior(
                            route[segmentIndex], route[segmentIndex + 1], NodeRect(node), out hit))
                        {
                            result.Add(
                                "layout.edge-through-node",
                                "连线穿过了非连接节点。",
                                node: node.Id,
                                edge: EdgeName(edge),
                                x: Round(hit.X),
                                y: Round(hit.Y));
                            if (result.Issues.Count >= MaxIssues)
                                return;
                        }
                    }
                }
            }
        }

        private static void ValidateEdgeIntersections(
            IList<EdgeInfo> edges,
            IDictionary<EdgeInfo, List<Pt>> routes,
            ValidationResult result)
        {
            for (int firstIndex = 0; firstIndex < edges.Count; firstIndex++)
            {
                EdgeInfo first = edges[firstIndex];
                List<Pt> firstRoute = routes[first];
                for (int secondIndex = firstIndex + 1; secondIndex < edges.Count; secondIndex++)
                {
                    EdgeInfo second = edges[secondIndex];
                    List<Pt> secondRoute = routes[second];
                    bool reportedPair = false;

                    for (int a = 0; a + 1 < firstRoute.Count && !reportedPair; a++)
                    {
                        for (int b = 0; b + 1 < secondRoute.Count; b++)
                        {
                            SegmentHit hit = IntersectSegments(
                                firstRoute[a], firstRoute[a + 1],
                                secondRoute[b], secondRoute[b + 1]);
                            if (hit.Kind == SegmentHitKind.None)
                                continue;

                            if (hit.Kind == SegmentHitKind.Point &&
                                IsAllowedSharedEndpoint(first, firstRoute, second, secondRoute, hit.Point))
                            {
                                continue;
                            }

                            result.Add(
                                hit.Kind == SegmentHitKind.Overlap
                                    ? "layout.edge-overlap"
                                    : "layout.edge-crossing",
                                hit.Kind == SegmentHitKind.Overlap
                                    ? "两条连线存在重叠线段。"
                                    : "两条没有共同连接点的连线发生交叉。",
                                edge: EdgeName(first),
                                otherEdge: EdgeName(second),
                                x: Round(hit.Point.X),
                                y: Round(hit.Point.Y));
                            reportedPair = true;
                            break;
                        }
                    }

                    if (result.Issues.Count >= MaxIssues)
                        return;
                }
            }
        }

        private static void ValidateLongSegments(
            GraphInfo graph,
            IDictionary<EdgeInfo, List<Pt>> routes,
            ValidationResult result)
        {
            double horizontalLimit = Math.Max(4.0, graph.Width * 0.60);
            foreach (KeyValuePair<EdgeInfo, List<Pt>> pair in routes)
            {
                List<Pt> route = pair.Value;
                for (int index = 0; index + 1 < route.Count; index++)
                {
                    double dx = Math.Abs(route[index + 1].X - route[index].X);
                    double dy = Math.Abs(route[index + 1].Y - route[index].Y);
                    if (dy < Epsilon && dx > horizontalLimit)
                    {
                        result.Add(
                            "layout.long-horizontal-segment",
                            "连线包含跨越页面主要宽度的水平长线；应缩短侧支或拆分页面。",
                            edge: EdgeName(pair.Key),
                            x: Round((route[index].X + route[index + 1].X) / 2.0),
                            y: Round(route[index].Y));
                        break;
                    }
                }
            }
        }

        private static void ValidateLabelPositions(
            GraphInfo graph,
            ValidationResult result)
        {
            foreach (EdgeInfo edge in graph.Edges)
            {
                if (string.IsNullOrWhiteSpace(edge.Label))
                    continue;

                foreach (NodeInfo node in graph.Nodes)
                {
                    if (node.Id == edge.From || node.Id == edge.To)
                        continue;
                    if (Contains(NodeRect(node), new Pt(edge.LabelX, edge.LabelY)))
                    {
                        result.Add(
                            "layout.label-on-node",
                            "连线标签落在了非连接节点内部。",
                            node: node.Id,
                            edge: EdgeName(edge),
                            x: Round(edge.LabelX),
                            y: Round(edge.LabelY));
                    }
                }
            }
        }

        private static SegmentHit IntersectSegments(Pt p, Pt p2, Pt q, Pt q2)
        {
            double rx = p2.X - p.X;
            double ry = p2.Y - p.Y;
            double sx = q2.X - q.X;
            double sy = q2.Y - q.Y;
            double denominator = Cross(rx, ry, sx, sy);
            double qpx = q.X - p.X;
            double qpy = q.Y - p.Y;

            if (Math.Abs(denominator) < Epsilon)
            {
                if (Math.Abs(Cross(qpx, qpy, rx, ry)) >= Epsilon)
                    return SegmentHit.None;

                double rr = rx * rx + ry * ry;
                if (rr < Epsilon * Epsilon)
                    return Distance(p, q) < Epsilon ? SegmentHit.At(p) : SegmentHit.None;

                double t0 = (qpx * rx + qpy * ry) / rr;
                double t1 = t0 + (sx * rx + sy * ry) / rr;
                double start = Math.Max(0.0, Math.Min(t0, t1));
                double end = Math.Min(1.0, Math.Max(t0, t1));
                if (end < start - Epsilon)
                    return SegmentHit.None;

                Pt midpoint = new Pt(
                    p.X + rx * ((start + end) / 2.0),
                    p.Y + ry * ((start + end) / 2.0));
                return end - start > Epsilon
                    ? SegmentHit.Overlap(midpoint)
                    : SegmentHit.At(midpoint);
            }

            double t = Cross(qpx, qpy, sx, sy) / denominator;
            double u = Cross(qpx, qpy, rx, ry) / denominator;
            if (t < -Epsilon || t > 1.0 + Epsilon || u < -Epsilon || u > 1.0 + Epsilon)
                return SegmentHit.None;

            return SegmentHit.At(new Pt(p.X + t * rx, p.Y + t * ry));
        }

        private static bool SegmentIntersectsRectangleInterior(Pt a, Pt b, Rect rect, out Pt hit)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double t0 = 0.0;
            double t1 = 1.0;

            if (!Clip(-dx, a.X - rect.Left, ref t0, ref t1) ||
                !Clip(dx, rect.Right - a.X, ref t0, ref t1) ||
                !Clip(-dy, a.Y - rect.Bottom, ref t0, ref t1) ||
                !Clip(dy, rect.Top - a.Y, ref t0, ref t1) ||
                t1 - t0 <= Epsilon)
            {
                hit = default(Pt);
                return false;
            }

            double middle = (t0 + t1) / 2.0;
            hit = new Pt(a.X + dx * middle, a.Y + dy * middle);
            return ContainsStrict(rect, hit);
        }

        private static bool Clip(double p, double q, ref double t0, ref double t1)
        {
            if (Math.Abs(p) < Epsilon)
                return q >= 0;

            double r = q / p;
            if (p < 0)
            {
                if (r > t1)
                    return false;
                if (r > t0)
                    t0 = r;
            }
            else
            {
                if (r < t0)
                    return false;
                if (r < t1)
                    t1 = r;
            }
            return true;
        }

        private static bool IsAllowedSharedEndpoint(
            EdgeInfo first,
            IList<Pt> firstRoute,
            EdgeInfo second,
            IList<Pt> secondRoute,
            Pt point)
        {
            return
                (first.From == second.From && Near(point, firstRoute[0]) && Near(point, secondRoute[0])) ||
                (first.To == second.To && Near(point, Last(firstRoute)) && Near(point, Last(secondRoute))) ||
                (first.To == second.From && Near(point, Last(firstRoute)) && Near(point, secondRoute[0])) ||
                (first.From == second.To && Near(point, firstRoute[0]) && Near(point, Last(secondRoute)));
        }

        private static Rect NodeRect(NodeInfo node)
        {
            return new Rect
            {
                Left = node.Cx - node.W / 2.0,
                Right = node.Cx + node.W / 2.0,
                Bottom = node.Cy - node.H / 2.0,
                Top = node.Cy + node.H / 2.0
            };
        }

        private static bool Contains(Rect rect, Pt point)
        {
            return point.X >= rect.Left && point.X <= rect.Right &&
                   point.Y >= rect.Bottom && point.Y <= rect.Top;
        }

        private static bool ContainsStrict(Rect rect, Pt point)
        {
            return point.X > rect.Left + Epsilon && point.X < rect.Right - Epsilon &&
                   point.Y > rect.Bottom + Epsilon && point.Y < rect.Top - Epsilon;
        }

        private static bool Near(Pt a, Pt b)
        {
            return Distance(a, b) < 0.01;
        }

        private static double Distance(Pt a, Pt b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static Pt Last(IList<Pt> points)
        {
            return points[points.Count - 1];
        }

        private static double Cross(double ax, double ay, double bx, double by)
        {
            return ax * by - ay * bx;
        }

        private static double Round(double value)
        {
            return Math.Round(value, 4);
        }

        private static string EdgeName(EdgeInfo edge)
        {
            return edge.From + " -> " + edge.To;
        }

        private struct Rect
        {
            public double Left;
            public double Right;
            public double Bottom;
            public double Top;
        }

        private enum SegmentHitKind
        {
            None,
            Point,
            Overlap
        }

        private struct SegmentHit
        {
            public SegmentHitKind Kind;
            public Pt Point;

            public static SegmentHit None => new SegmentHit { Kind = SegmentHitKind.None };
            public static SegmentHit At(Pt point) => new SegmentHit { Kind = SegmentHitKind.Point, Point = point };
            public static SegmentHit Overlap(Pt point) => new SegmentHit { Kind = SegmentHitKind.Overlap, Point = point };
        }
    }
}
