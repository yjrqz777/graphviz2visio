using System;
using System.Collections.Generic;
using Graphviz2Visio.Core.Models;

namespace Graphviz2Visio.Core.Utils
{
    /// <summary>
    /// Deterministic Manhattan router. Graphviz decides node placement; this class owns edge geometry.
    /// </summary>
    public static class OrthogonalRouter
    {
        private const double Epsilon = 0.001;
        private const double Clearance = 0.18;
        private const double OuterLane = 0.70;
        private const double BendPenalty = 0.30;
        private const double CrossingPenalty = 1000.0;

        public static Dictionary<EdgeInfo, List<Pt>> Route(GraphInfo graph)
        {
            if (graph == null)
                throw new ArgumentNullException(nameof(graph));

            var nodes = new Dictionary<string, NodeInfo>(StringComparer.Ordinal);
            foreach (NodeInfo node in graph.Nodes)
                nodes[node.Id] = node;

            var specs = BuildEndpointSpecs(graph.Edges, nodes);
            AssignPortSlots(specs);

            var obstacles = new List<Rect>();
            foreach (NodeInfo node in graph.Nodes)
                obstacles.Add(ExpandedNodeRect(node));

            var result = new Dictionary<EdgeInfo, List<Pt>>();
            var routed = new List<RoutedPath>();
            foreach (EndpointSpec spec in specs)
            {
                List<Pt> core = FindPath(spec.SourceEscape, spec.TargetEscape, obstacles, routed, true);
                if (core == null)
                    core = FindPath(spec.SourceEscape, spec.TargetEscape, obstacles, routed, false);
                if (core == null)
                    core = CreateFallback(spec.SourceEscape, spec.TargetEscape);

                var complete = new List<Pt>();
                AddPoint(complete, spec.SourcePort);
                foreach (Pt point in core)
                    AddPoint(complete, point);
                AddPoint(complete, spec.TargetPort);
                complete = Simplify(complete);

                result[spec.Edge] = complete;
                routed.Add(new RoutedPath { Edge = spec.Edge, Points = complete });
            }

            return result;
        }

        private static List<EndpointSpec> BuildEndpointSpecs(
            IList<EdgeInfo> edges,
            IDictionary<string, NodeInfo> nodes)
        {
            var specs = new List<EndpointSpec>();
            for (int index = 0; index < edges.Count; index++)
            {
                EdgeInfo edge = edges[index];
                NodeInfo source;
                NodeInfo target;
                nodes.TryGetValue(edge.From ?? string.Empty, out source);
                nodes.TryGetValue(edge.To ?? string.Empty, out target);
                if (source == null || target == null)
                    continue;

                Direction sourceDirection = InferDirection(source, target, edge.Points, true);
                Direction targetDirection = InferDirection(target, source, edge.Points, false);
                specs.Add(new EndpointSpec
                {
                    Edge = edge,
                    Index = index,
                    Source = source,
                    Target = target,
                    SourceDirection = sourceDirection,
                    TargetDirection = targetDirection
                });
            }
            return specs;
        }

        private static Direction InferDirection(
            NodeInfo node,
            NodeInfo other,
            IList<Pt> graphvizPoints,
            bool source)
        {
            Pt hint = new Pt(other.Cx, other.Cy);
            if (graphvizPoints != null && graphvizPoints.Count > 0)
                hint = source ? graphvizPoints[0] : graphvizPoints[graphvizPoints.Count - 1];

            double dx = hint.X - node.Cx;
            double dy = hint.Y - node.Cy;
            if (Math.Abs(dx) < Epsilon && Math.Abs(dy) < Epsilon)
            {
                dx = other.Cx - node.Cx;
                dy = other.Cy - node.Cy;
            }

            if (Math.Abs(dx) > Math.Abs(dy))
                return dx >= 0 ? Direction.East : Direction.West;
            return dy >= 0 ? Direction.North : Direction.South;
        }

        private static void AssignPortSlots(IList<EndpointSpec> specs)
        {
            var groups = new Dictionary<string, List<EndpointRef>>(StringComparer.Ordinal);
            foreach (EndpointSpec spec in specs)
            {
                AddEndpoint(groups, spec.Source, spec.SourceDirection, spec, true);
                AddEndpoint(groups, spec.Target, spec.TargetDirection, spec, false);
            }

            foreach (List<EndpointRef> group in groups.Values)
            {
                group.Sort((a, b) =>
                {
                    NodeInfo aOther = a.IsSource ? a.Spec.Target : a.Spec.Source;
                    NodeInfo bOther = b.IsSource ? b.Spec.Target : b.Spec.Source;
                    bool horizontalSide = a.Direction == Direction.North || a.Direction == Direction.South;
                    double av = horizontalSide ? aOther.Cx : aOther.Cy;
                    double bv = horizontalSide ? bOther.Cx : bOther.Cy;
                    int comparison = av.CompareTo(bv);
                    return comparison != 0 ? comparison : a.Spec.Index.CompareTo(b.Spec.Index);
                });

                for (int index = 0; index < group.Count; index++)
                {
                    EndpointRef endpoint = group[index];
                    double slot = group.Count == 1 ? 0.0 :
                        ((double)index / (group.Count - 1) - 0.5) * 0.64;
                    Pt port = BoundaryPort(endpoint.Node, endpoint.Direction, slot);
                    Pt escape = EscapeFromNode(endpoint.Node, port, endpoint.Direction);
                    if (endpoint.IsSource)
                    {
                        endpoint.Spec.SourcePort = port;
                        endpoint.Spec.SourceEscape = escape;
                    }
                    else
                    {
                        endpoint.Spec.TargetPort = port;
                        endpoint.Spec.TargetEscape = escape;
                    }
                }
            }
        }

        private static void AddEndpoint(
            IDictionary<string, List<EndpointRef>> groups,
            NodeInfo node,
            Direction direction,
            EndpointSpec spec,
            bool isSource)
        {
            string key = node.Id + "|" + direction;
            List<EndpointRef> group;
            if (!groups.TryGetValue(key, out group))
            {
                group = new List<EndpointRef>();
                groups[key] = group;
            }
            group.Add(new EndpointRef
            {
                Node = node,
                Direction = direction,
                Spec = spec,
                IsSource = isSource
            });
        }

        private static Pt BoundaryPort(NodeInfo node, Direction direction, double slot)
        {
            double hw = Math.Max(node.W / 2.0, Epsilon);
            double hh = Math.Max(node.H / 2.0, Epsilon);
            bool horizontalSide = direction == Direction.North || direction == Direction.South;
            double x = horizontalSide ? node.Cx + slot * hw : node.Cx;
            double y = horizontalSide ? node.Cy : node.Cy + slot * hh;
            string shape = (node.Shape ?? "box").ToLowerInvariant();

            if (shape == "diamond")
            {
                if (horizontalSide)
                    y = node.Cy + Sign(direction) * hh * (1.0 - Math.Abs((x - node.Cx) / hw));
                else
                    x = node.Cx + Sign(direction) * hw * (1.0 - Math.Abs((y - node.Cy) / hh));
            }
            else if (shape == "ellipse" || shape == "oval")
            {
                if (horizontalSide)
                    y = node.Cy + Sign(direction) * hh * Math.Sqrt(Math.Max(0.0, 1.0 - Square((x - node.Cx) / hw)));
                else
                    x = node.Cx + Sign(direction) * hw * Math.Sqrt(Math.Max(0.0, 1.0 - Square((y - node.Cy) / hh)));
            }
            else
            {
                if (horizontalSide)
                    y = node.Cy + Sign(direction) * hh;
                else
                    x = node.Cx + Sign(direction) * hw;
            }

            return new Pt(x, y);
        }

        private static List<Pt> FindPath(
            Pt start,
            Pt finish,
            IList<Rect> obstacles,
            IList<RoutedPath> existing,
            bool forbidConflicts)
        {
            var xs = new List<double> { start.X, finish.X };
            var ys = new List<double> { start.Y, finish.Y };
            double minX = Math.Min(start.X, finish.X);
            double maxX = Math.Max(start.X, finish.X);
            double minY = Math.Min(start.Y, finish.Y);
            double maxY = Math.Max(start.Y, finish.Y);
            foreach (Rect rect in obstacles)
            {
                xs.Add(rect.Left);
                xs.Add(rect.Right);
                ys.Add(rect.Bottom);
                ys.Add(rect.Top);
                minX = Math.Min(minX, rect.Left);
                maxX = Math.Max(maxX, rect.Right);
                minY = Math.Min(minY, rect.Bottom);
                maxY = Math.Max(maxY, rect.Top);
            }
            int outerLaneCount = Math.Min(6, existing.Count + 1);
            for (int lane = 1; lane <= outerLaneCount; lane++)
            {
                xs.Add(minX - OuterLane * lane);
                xs.Add(maxX + OuterLane * lane);
                ys.Add(minY - OuterLane * lane);
                ys.Add(maxY + OuterLane * lane);
            }
            AddMidpoints(xs);
            AddMidpoints(ys);
            xs = UniqueSorted(xs);
            ys = UniqueSorted(ys);

            int width = xs.Count;
            int height = ys.Count;
            int startX = NearestIndex(xs, start.X);
            int startY = NearestIndex(ys, start.Y);
            int finishX = NearestIndex(xs, finish.X);
            int finishY = NearestIndex(ys, finish.Y);
            int stateCount = width * height * 3;
            var distance = new double[stateCount];
            var previous = new int[stateCount];
            var visited = new bool[stateCount];
            for (int i = 0; i < stateCount; i++)
            {
                distance[i] = double.PositiveInfinity;
                previous[i] = -1;
            }

            int startState = StateIndex(startX, startY, 0, width);
            distance[startState] = 0.0;
            var heap = new MinHeap();
            heap.Push(startState, 0.0);
            int finalState = -1;

            while (heap.Count > 0)
            {
                HeapEntry entry = heap.Pop();
                int state = entry.State;
                if (visited[state])
                    continue;
                visited[state] = true;

                int direction = state % 3;
                int pointIndex = state / 3;
                int xIndex = pointIndex % width;
                int yIndex = pointIndex / width;
                if (xIndex == finishX && yIndex == finishY)
                {
                    finalState = state;
                    break;
                }

                TryNeighbor(xIndex - 1, yIndex, 1);
                TryNeighbor(xIndex + 1, yIndex, 1);
                TryNeighbor(xIndex, yIndex - 1, 2);
                TryNeighbor(xIndex, yIndex + 1, 2);

                void TryNeighbor(int nextX, int nextY, int nextDirection)
                {
                    if (nextX < 0 || nextX >= width || nextY < 0 || nextY >= height)
                        return;
                    Pt a = new Pt(xs[xIndex], ys[yIndex]);
                    Pt b = new Pt(xs[nextX], ys[nextY]);
                    if (PointInsideObstacle(b, obstacles) || SegmentHitsObstacle(a, b, obstacles))
                        return;

                    int conflicts = CountConflicts(a, b, existing);
                    if (forbidConflicts && conflicts > 0)
                        return;

                    double cost = Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
                    if (direction != 0 && direction != nextDirection)
                        cost += BendPenalty;
                    if (!forbidConflicts)
                        cost += conflicts * CrossingPenalty;

                    int nextState = StateIndex(nextX, nextY, nextDirection, width);
                    double nextDistance = distance[state] + cost;
                    if (nextDistance + Epsilon >= distance[nextState])
                        return;
                    distance[nextState] = nextDistance;
                    previous[nextState] = state;
                    heap.Push(nextState, nextDistance);
                }
            }

            if (finalState < 0)
                return null;

            var reversed = new List<Pt>();
            for (int state = finalState; state >= 0; state = previous[state])
            {
                int pointIndex = state / 3;
                reversed.Add(new Pt(xs[pointIndex % width], ys[pointIndex / width]));
                if (state == startState)
                    break;
            }
            reversed.Reverse();
            return Simplify(reversed);
        }

        private static int CountConflicts(Pt a, Pt b, IList<RoutedPath> existing)
        {
            int count = 0;
            foreach (RoutedPath route in existing)
            {
                for (int index = 0; index + 1 < route.Points.Count; index++)
                {
                    if (SegmentsConflict(a, b, route.Points[index], route.Points[index + 1]))
                        count++;
                }
            }
            return count;
        }

        private static bool SegmentsConflict(Pt a, Pt b, Pt c, Pt d)
        {
            bool abHorizontal = Near(a.Y, b.Y);
            bool cdHorizontal = Near(c.Y, d.Y);
            if (abHorizontal && cdHorizontal)
            {
                if (!Near(a.Y, c.Y))
                    return false;
                return OverlapLength(a.X, b.X, c.X, d.X) > Epsilon;
            }
            if (!abHorizontal && !cdHorizontal)
            {
                if (!Near(a.X, c.X))
                    return false;
                return OverlapLength(a.Y, b.Y, c.Y, d.Y) > Epsilon;
            }

            Pt h1 = abHorizontal ? a : c;
            Pt h2 = abHorizontal ? b : d;
            Pt v1 = abHorizontal ? c : a;
            Pt v2 = abHorizontal ? d : b;
            double ix = v1.X;
            double iy = h1.Y;
            if (!Between(ix, h1.X, h2.X) || !Between(iy, v1.Y, v2.Y))
                return false;

            Pt intersection = new Pt(ix, iy);
            bool atBothEnds = (Near(intersection, a) || Near(intersection, b)) &&
                              (Near(intersection, c) || Near(intersection, d));
            return !atBothEnds;
        }

        private static bool SegmentHitsObstacle(Pt a, Pt b, IList<Rect> obstacles)
        {
            foreach (Rect rect in obstacles)
            {
                if (Near(a.Y, b.Y))
                {
                    if (a.Y > rect.Bottom + Epsilon && a.Y < rect.Top - Epsilon &&
                        OverlapLength(a.X, b.X, rect.Left, rect.Right) > Epsilon)
                        return true;
                }
                else if (a.X > rect.Left + Epsilon && a.X < rect.Right - Epsilon &&
                         OverlapLength(a.Y, b.Y, rect.Bottom, rect.Top) > Epsilon)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool PointInsideObstacle(Pt point, IList<Rect> obstacles)
        {
            foreach (Rect rect in obstacles)
            {
                if (point.X > rect.Left + Epsilon && point.X < rect.Right - Epsilon &&
                    point.Y > rect.Bottom + Epsilon && point.Y < rect.Top - Epsilon)
                    return true;
            }
            return false;
        }

        private static List<Pt> CreateFallback(Pt start, Pt finish)
        {
            var route = new List<Pt> { start };
            if (!Near(start.X, finish.X) && !Near(start.Y, finish.Y))
                route.Add(new Pt(start.X, finish.Y));
            route.Add(finish);
            return route;
        }

        private static List<Pt> Simplify(IList<Pt> points)
        {
            var result = new List<Pt>();
            foreach (Pt point in points)
            {
                AddPoint(result, point);
                while (result.Count >= 3)
                {
                    Pt a = result[result.Count - 3];
                    Pt b = result[result.Count - 2];
                    Pt c = result[result.Count - 1];
                    if ((!Near(a.X, b.X) || !Near(b.X, c.X)) &&
                        (!Near(a.Y, b.Y) || !Near(b.Y, c.Y)))
                        break;
                    result.RemoveAt(result.Count - 2);
                }
            }
            return result;
        }

        private static Rect ExpandedNodeRect(NodeInfo node)
        {
            return new Rect
            {
                Left = node.Cx - node.W / 2.0 - Clearance,
                Right = node.Cx + node.W / 2.0 + Clearance,
                Bottom = node.Cy - node.H / 2.0 - Clearance,
                Top = node.Cy + node.H / 2.0 + Clearance
            };
        }

        private static List<double> UniqueSorted(List<double> values)
        {
            values.Sort();
            var result = new List<double>();
            foreach (double value in values)
            {
                if (result.Count == 0 || !Near(result[result.Count - 1], value))
                    result.Add(value);
            }
            return result;
        }

        private static void AddMidpoints(List<double> values)
        {
            List<double> sorted = UniqueSorted(new List<double>(values));
            for (int index = 0; index + 1 < sorted.Count; index++)
            {
                if (sorted[index + 1] - sorted[index] >= Clearance * 2.0)
                    values.Add((sorted[index] + sorted[index + 1]) / 2.0);
            }
        }

        private static int NearestIndex(IList<double> values, double target)
        {
            int best = 0;
            double distance = double.PositiveInfinity;
            for (int index = 0; index < values.Count; index++)
            {
                double candidate = Math.Abs(values[index] - target);
                if (candidate < distance)
                {
                    distance = candidate;
                    best = index;
                }
            }
            return best;
        }

        private static int StateIndex(int x, int y, int direction, int width)
        {
            return ((y * width) + x) * 3 + direction;
        }

        private static Pt Move(Pt point, Direction direction, double distance)
        {
            switch (direction)
            {
                case Direction.North: return new Pt(point.X, point.Y + distance);
                case Direction.South: return new Pt(point.X, point.Y - distance);
                case Direction.East: return new Pt(point.X + distance, point.Y);
                default: return new Pt(point.X - distance, point.Y);
            }
        }

        private static Pt EscapeFromNode(NodeInfo node, Pt port, Direction direction)
        {
            switch (direction)
            {
                case Direction.North:
                    return new Pt(port.X, node.Cy + node.H / 2.0 + Clearance);
                case Direction.South:
                    return new Pt(port.X, node.Cy - node.H / 2.0 - Clearance);
                case Direction.East:
                    return new Pt(node.Cx + node.W / 2.0 + Clearance, port.Y);
                default:
                    return new Pt(node.Cx - node.W / 2.0 - Clearance, port.Y);
            }
        }

        private static double Sign(Direction direction)
        {
            return direction == Direction.North || direction == Direction.East ? 1.0 : -1.0;
        }

        private static double Square(double value) => value * value;
        private static bool Near(double a, double b) => Math.Abs(a - b) < Epsilon;
        private static bool Near(Pt a, Pt b) => Near(a.X, b.X) && Near(a.Y, b.Y);
        private static bool Between(double value, double a, double b) =>
            value >= Math.Min(a, b) - Epsilon && value <= Math.Max(a, b) + Epsilon;
        private static double OverlapLength(double a1, double a2, double b1, double b2) =>
            Math.Max(0.0, Math.Min(Math.Max(a1, a2), Math.Max(b1, b2)) -
                          Math.Max(Math.Min(a1, a2), Math.Min(b1, b2)));

        private static void AddPoint(ICollection<Pt> points, Pt point)
        {
            Pt last = default(Pt);
            bool any = false;
            foreach (Pt existing in points)
            {
                last = existing;
                any = true;
            }
            if (!any || !Near(last, point))
                points.Add(point);
        }

        private enum Direction { North, South, East, West }

        private sealed class EndpointSpec
        {
            public EdgeInfo Edge;
            public int Index;
            public NodeInfo Source;
            public NodeInfo Target;
            public Direction SourceDirection;
            public Direction TargetDirection;
            public Pt SourcePort;
            public Pt TargetPort;
            public Pt SourceEscape;
            public Pt TargetEscape;
        }

        private sealed class EndpointRef
        {
            public NodeInfo Node;
            public Direction Direction;
            public EndpointSpec Spec;
            public bool IsSource;
        }

        private sealed class RoutedPath
        {
            public EdgeInfo Edge;
            public List<Pt> Points;
        }

        private struct Rect
        {
            public double Left;
            public double Right;
            public double Bottom;
            public double Top;
        }

        private struct HeapEntry
        {
            public int State;
            public double Priority;
        }

        private sealed class MinHeap
        {
            private readonly List<HeapEntry> _items = new List<HeapEntry>();
            public int Count => _items.Count;

            public void Push(int state, double priority)
            {
                _items.Add(new HeapEntry { State = state, Priority = priority });
                int index = _items.Count - 1;
                while (index > 0)
                {
                    int parent = (index - 1) / 2;
                    if (_items[parent].Priority <= priority)
                        break;
                    _items[index] = _items[parent];
                    index = parent;
                }
                _items[index] = new HeapEntry { State = state, Priority = priority };
            }

            public HeapEntry Pop()
            {
                HeapEntry root = _items[0];
                HeapEntry tail = _items[_items.Count - 1];
                _items.RemoveAt(_items.Count - 1);
                if (_items.Count == 0)
                    return root;

                int index = 0;
                while (true)
                {
                    int left = index * 2 + 1;
                    if (left >= _items.Count)
                        break;
                    int right = left + 1;
                    int child = right < _items.Count && _items[right].Priority < _items[left].Priority
                        ? right : left;
                    if (_items[child].Priority >= tail.Priority)
                        break;
                    _items[index] = _items[child];
                    index = child;
                }
                _items[index] = tail;
                return root;
            }
        }
    }
}
