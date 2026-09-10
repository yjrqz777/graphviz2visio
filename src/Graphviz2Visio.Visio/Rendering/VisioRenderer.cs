using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Graphviz2Visio.Core.Models;
using Graphviz2Visio.Core.Parsing;
using Graphviz2Visio.Core.Utils;

namespace Graphviz2Visio.Visio.Rendering
{
    public static class VisioRenderer
    {
        public static void RenderPlainToVisio(string plainPath, string outputVsdxPath, bool visible = false)
        {
            RenderPlainFilesToVisio(new[] { plainPath }, outputVsdxPath, visible);
        }

        public static void RenderPlainFilesToVisio(IEnumerable<string> plainPaths, string outputVsdxPath, bool visible = false)
        {
            RenderPlainFilesToVisio(plainPaths, outputVsdxPath, visible, null);
        }

        public static void RenderPlainFilesToVisio(
            IEnumerable<string> plainPaths,
            string outputVsdxPath,
            bool visible,
            IEnumerable<string> requestedPageNames)
        {
            if (plainPaths == null)
                throw new ArgumentNullException(nameof(plainPaths));

            var plainFilePaths = new List<string>();
            foreach (string plainPath in plainPaths)
            {
                if (string.IsNullOrWhiteSpace(plainPath))
                    throw new ArgumentException("Plain file path cannot be empty.", nameof(plainPaths));
                if (!File.Exists(plainPath))
                    throw new FileNotFoundException("Plain file not found.", plainPath);

                plainFilePaths.Add(plainPath);
            }

            if (plainFilePaths.Count == 0)
                throw new ArgumentException("At least one plain file is required.", nameof(plainPaths));

            var explicitPageNames = new List<string>();
            if (requestedPageNames != null)
            {
                foreach (string pageName in requestedPageNames)
                {
                    if (string.IsNullOrWhiteSpace(pageName))
                        throw new ArgumentException("Page name cannot be empty.", nameof(requestedPageNames));

                    explicitPageNames.Add(pageName.Trim());
                }

                if (explicitPageNames.Count != plainFilePaths.Count)
                    throw new ArgumentException("One page name is required for every plain file.", nameof(requestedPageNames));
            }

            var pageNames = new List<string>();
            for (int index = 0; index < plainFilePaths.Count; index++)
            {
                string plainPath = plainFilePaths[index];
                pageNames.Add(requestedPageNames == null
                    ? Path.GetFileNameWithoutExtension(plainPath)
                    : explicitPageNames[index]);
            }
            ValidatePageNames(pageNames);

            string outDir = Path.GetDirectoryName(Path.GetFullPath(outputVsdxPath));
            if (!string.IsNullOrWhiteSpace(outDir) && !Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);

            object app = null;
            object doc = null;
            object flowchartStencil = null;
            object decisionMaster = null;
            object pages = null;

            try
            {
                Type visioType = Type.GetTypeFromProgID("Visio.Application");
                if (visioType == null)
                    throw new InvalidOperationException("Visio.Application was not found. Please confirm Microsoft Visio is installed.");

                dynamic dapp = Activator.CreateInstance(visioType);
                app = dapp;
                dapp.Visible = visible ? 1 : 0;

                dynamic ddoc = dapp.Documents.Add("");
                doc = ddoc;

                dynamic dpages = ddoc.Pages;
                pages = dpages;
                dynamic dDecisionMaster = null;

                for (int index = 0; index < plainFilePaths.Count; index++)
                {
                    GraphInfo graph = PlainParser.Parse(plainFilePaths[index]);
                    if (dDecisionMaster == null && ContainsDiamond(graph))
                    {
                        dynamic dStencil = OpenFlowchartStencil(dapp);
                        flowchartStencil = dStencil;
                        dDecisionMaster = FindDecisionMaster(dStencil);
                        decisionMaster = dDecisionMaster;
                    }

                    object page = null;
                    try
                    {
                        dynamic dpage = index == 0 ? dapp.ActivePage : dpages.Add();
                        page = dpage;
                        SetPageName(dpage, pageNames[index], index + 1);
                        RenderGraphToPage(dpage, graph, dDecisionMaster);
                    }
                    finally
                    {
                        ReleaseCom(page);
                    }
                }

                ddoc.SaveAs(Path.GetFullPath(outputVsdxPath));
            }
            finally
            {
                ReleaseCom(decisionMaster);
                ReleaseCom(pages);
                if (flowchartStencil != null)
                {
                    try { ((dynamic)flowchartStencil).Close(); } catch { }
                    ReleaseCom(flowchartStencil);
                }
                if (doc != null)
                {
                    try { ((dynamic)doc).Close(); } catch { }
                }
                if (app != null)
                {
                    try { ((dynamic)app).Quit(); } catch { }
                }
                ReleaseCom(doc);
                ReleaseCom(app);
            }
        }

        private static void ValidatePageNames(IEnumerable<string> pageNames)
        {
            var uniqueNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string pageName in pageNames)
            {
                if (string.IsNullOrWhiteSpace(pageName))
                    throw new ArgumentException("Page name cannot be empty.", nameof(pageNames));
                if (pageName.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
                    throw new ArgumentException("Page name contains an invalid character: " + pageName, nameof(pageNames));
                if (!uniqueNames.Add(pageName))
                    throw new ArgumentException("Page names must be unique: " + pageName, nameof(pageNames));
            }
        }

        private static bool ContainsDiamond(GraphInfo graph)
        {
            foreach (NodeInfo node in graph.Nodes)
            {
                if (string.Equals(node.Shape, "diamond", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static void SetPageName(dynamic page, string suggestedName, int pageNumber)
        {
            string pageName = string.IsNullOrWhiteSpace(suggestedName)
                ? "Flow " + pageNumber.ToString(CultureInfo.InvariantCulture)
                : suggestedName;

            try
            {
                page.Name = pageName;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Unable to set Visio page " + pageNumber.ToString(CultureInfo.InvariantCulture) +
                    " name to '" + pageName + "'.",
                    ex);
            }
        }

        private static void RenderGraphToPage(dynamic page, GraphInfo graph, dynamic decisionMaster)
        {
            var nodesById = new Dictionary<string, NodeInfo>(StringComparer.Ordinal);
            foreach (var node in graph.Nodes)
                nodesById[node.Id] = node;

            var routesByEdge = CreateRoutes(graph.Edges, nodesById);
            var offset = PreparePage((object)page, graph, routesByEdge, 1.0);
            double offsetX = offset.offsetX;
            double offsetY = offset.offsetY;

            foreach (var edge in graph.Edges)
                DrawEdge(page, edge, nodesById, routesByEdge[edge], offsetX, offsetY);
            foreach (var node in graph.Nodes)
                DrawNode(page, node, offsetX, offsetY, decisionMaster);
        }
        private static dynamic OpenFlowchartStencil(dynamic app)
        {
            var candidates = new List<string>();

            // visBuiltInStencilFlowchart = 4, visMSDefault = -2
            try
            {
                string builtIn = app.GetBuiltInStencilFile(4, -2);
                if (!string.IsNullOrWhiteSpace(builtIn))
                    candidates.Add(builtIn);
            }
            catch
            {
            }

            candidates.AddRange(new[]
            {
                "BASFLO_M.VSSX",
                "BASFLO_U.VSSX",
                "Basic Flowchart Shapes.vssx",
                "Basic Flowchart Shapes.vss",
                "基本流程图形状.vssx"
            });

            // visOpenRO (2) | visOpenHidden (64) = 66
            foreach (var name in candidates)
            {
                try
                {
                    return app.Documents.OpenEx(name, (short)66);
                }
                catch
                {
                }
            }
            return null;
        }

        /// <summary>
        /// 从模具中查找"判定/Decision" master，找不到返回 null。
        /// </summary>
        private static dynamic FindDecisionMaster(dynamic stencil)
        {
            if (stencil == null)
                return null;

            // 优先用统一名称 NameU（跨语言稳定）
            try { return stencil.Masters.ItemU("Decision"); } catch { }

            // 回退到本地化名称
            string[] localNames = { "Decision", "判定", "决定", "判断" };
            foreach (var n in localNames)
            {
                try { return stencil.Masters[n]; } catch { }
            }
            return null;
        }

        private static (double offsetX, double offsetY) PreparePage(
            object pageObj,
            GraphInfo graph,
            IDictionary<EdgeInfo, List<Pt>> routesByEdge,
            double margin)
        {
            dynamic page = pageObj;

            double minX = double.PositiveInfinity;
            double minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity;
            double maxY = double.NegativeInfinity;

            foreach (var n in graph.Nodes)
            {
                minX = Math.Min(minX, n.Cx - n.W / 2.0);
                minY = Math.Min(minY, n.Cy - n.H / 2.0);
                maxX = Math.Max(maxX, n.Cx + n.W / 2.0);
                maxY = Math.Max(maxY, n.Cy + n.H / 2.0);
            }

            foreach (var e in graph.Edges)
            {
                List<Pt> route;
                if (!routesByEdge.TryGetValue(e, out route))
                    continue;

                foreach (var p in route)
                {
                    minX = Math.Min(minX, p.X);
                    minY = Math.Min(minY, p.Y);
                    maxX = Math.Max(maxX, p.X);
                    maxY = Math.Max(maxY, p.Y);
                }

                if (!string.IsNullOrWhiteSpace(e.Label))
                {
                    minX = Math.Min(minX, e.LabelX - 0.4);
                    maxX = Math.Max(maxX, e.LabelX + 0.4);
                    minY = Math.Min(minY, e.LabelY - 0.2);
                    maxY = Math.Max(maxY, e.LabelY + 0.2);
                }
            }

            if (double.IsInfinity(minX))
            {
                minX = minY = 0;
                maxX = maxY = 1;
            }

            double contentW = maxX - minX;
            double contentH = maxY - minY;

            double pageW = Math.Max(8.27, contentW + margin * 2);
            double pageH = Math.Max(11.69, contentH + margin * 2);

            object pageSheet = null;
            object pageWidthCell = null;
            object pageHeightCell = null;
            try
            {
                dynamic dPageSheet = page.PageSheet;
                pageSheet = dPageSheet;
                dynamic dPageWidthCell = dPageSheet.CellsU["PageWidth"];
                dynamic dPageHeightCell = dPageSheet.CellsU["PageHeight"];
                pageWidthCell = dPageWidthCell;
                pageHeightCell = dPageHeightCell;
                dPageWidthCell.ResultIU = pageW;
                dPageHeightCell.ResultIU = pageH;
            }
            finally
            {
                ReleaseCom(pageHeightCell);
                ReleaseCom(pageWidthCell);
                ReleaseCom(pageSheet);
            }

            double offsetX = (pageW - contentW) / 2.0 - minX;
            double offsetY = (pageH - contentH) / 2.0 - minY;

            return (offsetX, offsetY);
        }

        private static void DrawNode(dynamic page, NodeInfo node, double ox, double oy, dynamic decisionMaster)
        {
            string shapeType = (node.Shape ?? "box").ToLowerInvariant();

            if (shapeType == "ellipse" || shapeType == "oval")
                DrawTerminatorNode(page, node, ox, oy);
            else if (shapeType == "diamond")
                DrawDiamondNode(page, node, ox, oy, decisionMaster);
            else
                DrawBoxNode(page, node, ox, oy);
        }
        private static void DrawBoxNode(dynamic page, NodeInfo node, double ox, double oy)
        {
            double cx = ox + node.Cx;
            double cy = oy + node.Cy;

            var shape = page.DrawRectangle(
                cx - node.W / 2.0,
                cy - node.H / 2.0,
                cx + node.W / 2.0,
                cy + node.H / 2.0);

            shape.Text = node.Label ?? node.Id;

            ApplyFillAndLine(shape, node.Color, node.FillColor);
            ApplyTextStyle(shape, 10.0);

            if ((node.Style ?? string.Empty).ToLowerInvariant().Contains("rounded"))
                SafeSetFormula(shape, "Rounding", "0.08 in");

            ReleaseCom(shape);
        }

        private static void DrawTerminatorNode(dynamic page, NodeInfo node, double ox, double oy)
        {
            double cx = ox + node.Cx;
            double cy = oy + node.Cy;

            var shape = page.DrawRectangle(
                cx - node.W / 2.0,
                cy - node.H / 2.0,
                cx + node.W / 2.0,
                cy + node.H / 2.0);

            shape.Text = node.Label ?? node.Id;
            ApplyFillAndLine(shape, "black", "#D9D9D9");
            ApplyTextStyle(shape, 10.0);
            SafeSetFormula(shape, "Rounding", "0.18 in");

            ReleaseCom(shape);
        }
        private static void DrawDiamondNode(dynamic page, NodeInfo node, double ox, double oy, dynamic decisionMaster)
        {
            double cx = ox + node.Cx;
            double cy = oy + node.Cy;

            // 优先使用 Visio 标准流程图模具中的"判定 (Decision)" master。
            if (decisionMaster != null)
            {
                dynamic shape = null;
                try
                {
                    shape = page.Drop(decisionMaster, cx, cy);

                    // master 默认尺寸来自模具，需按 graphviz 给出的宽高重设。
                    SafeSetFormula(shape, "Width", node.W.ToString(CultureInfo.InvariantCulture) + " in");
                    SafeSetFormula(shape, "Height", node.H.ToString(CultureInfo.InvariantCulture) + " in");
                    SafeSetFormula(shape, "PinX", cx.ToString(CultureInfo.InvariantCulture) + " in");
                    SafeSetFormula(shape, "PinY", cy.ToString(CultureInfo.InvariantCulture) + " in");

                    shape.Text = node.Label ?? node.Id;
                    ApplyFillAndLine(shape, node.Color, node.FillColor);
                    ApplyTextStyle(shape, 10.0);
                    return;
                }
                catch
                {
                    // master Drop 失败则回退到手绘菱形。
                }
                finally
                {
                    if (shape != null)
                        ReleaseCom(shape);
                }
            }

            DrawDiamondNodeFallback(page, node, cx, cy);
        }

        /// <summary>
        /// 兜底实现：用 4 条直线 + 透明文字框拼一个菱形。仅在找不到 Visio 判定 master 时使用。
        /// </summary>
        private static void DrawDiamondNodeFallback(dynamic page, NodeInfo node, double cx, double cy)
        {
            double hw = node.W / 2.0;
            double hh = node.H / 2.0;

            Pt top = new Pt(cx, cy + hh);
            Pt right = new Pt(cx + hw, cy);
            Pt bottom = new Pt(cx, cy - hh);
            Pt left = new Pt(cx - hw, cy);

            var l1 = DrawSimpleLine(page, top, right, node.Color, false, false);
            var l2 = DrawSimpleLine(page, right, bottom, node.Color, false, false);
            var l3 = DrawSimpleLine(page, bottom, left, node.Color, false, false);
            var l4 = DrawSimpleLine(page, left, top, node.Color, false, false);

            var labelBox = page.DrawRectangle(
                cx - node.W * 0.28,
                cy - node.H * 0.22,
                cx + node.W * 0.28,
                cy + node.H * 0.22);

            labelBox.Text = node.Label ?? node.Id;
            SafeSetFormula(labelBox, "LinePattern", "0");
            SafeSetFormula(labelBox, "FillPattern", "0");
            ApplyTextStyle(labelBox, 10.0);

            ReleaseCom(labelBox);
            ReleaseCom(l1);
            ReleaseCom(l2);
            ReleaseCom(l3);
            ReleaseCom(l4);
        }

        private static void DrawEdge(
            dynamic page,
            EdgeInfo edge,
            IDictionary<string, NodeInfo> nodesById,
            List<Pt> route,
            double ox,
            double oy)
        {
            NodeInfo sourceNode;
            nodesById.TryGetValue(edge.From ?? string.Empty, out sourceNode);
            if (route.Count < 2)
                return;

            bool dashed = string.Equals(edge.Style, "dashed", StringComparison.OrdinalIgnoreCase);
            string displayLabel = GetEdgeDisplayLabel(edge, sourceNode, route);
            bool hasLabel = !string.IsNullOrWhiteSpace(displayLabel);
            bool isDecisionBranch = IsDecisionShape(sourceNode) && hasLabel;
            int labelSegmentIndex = hasLabel && !isDecisionBranch
                ? FindNearestSegmentIndex(route, edge.LabelX, edge.LabelY)
                : -1;

            for (int index = 0; index < route.Count - 1; index++)
            {
                Pt startPoint = OffsetPoint(route[index], ox, oy);
                Pt endPoint = OffsetPoint(route[index + 1], ox, oy);
                bool endArrow = index == route.Count - 2;

                dynamic line = DrawSimpleLine(page, startPoint, endPoint, "black", dashed, endArrow);
                try
                {
                    if (index == labelSegmentIndex)
                    {
                        AttachLabelToSegment(
                            line,
                            startPoint,
                            endPoint,
                            ox + edge.LabelX,
                            oy + edge.LabelY,
                            displayLabel);
                    }
                }
                finally
                {
                    ReleaseCom(line);
                }
            }
            if (isDecisionBranch)
                DrawDecisionBranchLabel(page, route, ox, oy, displayLabel);
        }

        private static string GetEdgeDisplayLabel(EdgeInfo edge, NodeInfo sourceNode, IList<Pt> route)
        {
            if (!string.IsNullOrWhiteSpace(edge.Label))
                return edge.Label.Trim();

            return string.Empty;
        }

        private static void DrawDecisionBranchLabel(dynamic page, IList<Pt> route, double ox, double oy, string label)
        {
            Pt sourcePort = OffsetPoint(route[0], ox, oy);
            Pt nextPoint = OffsetPoint(route[1], ox, oy);
            double dx = nextPoint.X - sourcePort.X;
            double dy = nextPoint.Y - sourcePort.Y;
            double labelX;
            double labelY;

            if (Math.Abs(dx) >= Math.Abs(dy))
            {
                labelX = sourcePort.X + (dx >= 0 ? 0.24 : -0.24);
                labelY = sourcePort.Y + 0.14;
            }
            else
            {
                labelX = sourcePort.X + 0.14;
                labelY = sourcePort.Y + (dy >= 0 ? 0.24 : -0.24);
            }

            dynamic labelShape = page.DrawRectangle(labelX - 0.11, labelY - 0.09, labelX + 0.11, labelY + 0.09);
            try
            {
                labelShape.Text = label;
                SafeSetFormula(labelShape, "LinePattern", "0");
                SafeSetFormula(labelShape, "FillPattern", "0");
                ApplyTextStyle(labelShape, 8.0);
            }
            finally
            {
                ReleaseCom(labelShape);
            }
        }
        private static Dictionary<EdgeInfo, List<Pt>> CreateRoutes(
            IEnumerable<EdgeInfo> edges,
            IDictionary<string, NodeInfo> nodesById)
        {
            var routesByEdge = new Dictionary<EdgeInfo, List<Pt>>();
            foreach (EdgeInfo edge in edges)
            {
                NodeInfo sourceNode;
                NodeInfo targetNode;
                nodesById.TryGetValue(edge.From ?? string.Empty, out sourceNode);
                nodesById.TryGetValue(edge.To ?? string.Empty, out targetNode);
                routesByEdge[edge] = CreateRoute(edge, sourceNode, targetNode);
            }

            return routesByEdge;
        }

        private static List<Pt> CreateRoute(EdgeInfo edge, NodeInfo sourceNode, NodeInfo targetNode)
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

            Pt sourcePort = AttachToNodeBoundary(sourceNode, new Pt(sourceNode.Cx, sourceNode.Cy), new Pt(targetNode.Cx, targetNode.Cy));
            Pt targetPort = AttachToNodeBoundary(targetNode, new Pt(targetNode.Cx, targetNode.Cy), new Pt(sourceNode.Cx, sourceNode.Cy));
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

        private static bool IsDecisionShape(NodeInfo node)
        {
            return node != null && string.Equals(node.Shape, "diamond", StringComparison.OrdinalIgnoreCase);
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

            if (!hasLastPoint || Math.Abs(lastPoint.X - point.X) > 0.001 || Math.Abs(lastPoint.Y - point.Y) > 0.001)
                route.Add(point);
        }

        /// <summary>
        /// 在折线 pts 的相邻段中，挑出"中点距离目标 (labelX, labelY) 最近"的段索引。
        /// </summary>
        private static int FindNearestSegmentIndex(List<Pt> pts, double labelX, double labelY)
        {
            int best = 0;
            double bestDist = double.PositiveInfinity;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                double mx = (pts[i].X + pts[i + 1].X) / 2.0;
                double my = (pts[i].Y + pts[i + 1].Y) / 2.0;
                double dx = mx - labelX;
                double dy = my - labelY;
                double d = dx * dx + dy * dy;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>
        /// 把 label 挂到一根 1D Shape 上作为它的 Text，并把文字定位到线条上。
        /// 沿线方向位置取自 graphviz 给的 (globalX, globalY) 在线条局部 X 轴上的投影
        /// (clamp 到段内)，垂直方向归零，让文字中心紧贴线段；同时给文本块加白底，
        /// 用于遮挡穿过文字中间的线。
        /// </summary>
        private static void AttachLabelToSegment(dynamic line, Pt segStart, Pt segEnd, double globalX, double globalY, string label)
        {
            line.Text = label;
            ApplyTextStyle(line, 8.0);

            // 让标签相对"页面"始终水平：TxtAngle 是相对 shape 局部坐标系的角度，
            // 而 1D Shape 的局部坐标系本身已沿 Begin→End 旋转；设为 -Angle 可抵消
            // shape 的旋转，避免线条朝左/朝下时文字被翻转或倒置。
            SafeSetFormula(line, "TxtAngle", "-Angle");

            double dx = segEnd.X - segStart.X;
            double dy = segEnd.Y - segStart.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6)
                return;

            double vx = dx / len;
            double vy = dy / len;
            double tx = globalX - segStart.X;
            double ty = globalY - segStart.Y;

            // 沿线方向投影；垂直方向直接归零，把文字中心贴回线上。
            double localX = tx * vx + ty * vy;
            double pinX = Math.Max(0.0, Math.Min(len, localX));

            SafeSetFormula(line, "TxtPinX", pinX.ToString(CultureInfo.InvariantCulture) + " in");
            SafeSetFormula(line, "TxtPinY", "0 in");
        }

        private static Pt OffsetPoint(Pt point, double ox, double oy)
        {
            return new Pt(ox + point.X, oy + point.Y);
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


        private static object DrawSimpleLine(dynamic page, Pt p1, Pt p2, string color, bool dashed, bool endArrow)
        {
            var line = page.DrawLine(p1.X, p1.Y, p2.X, p2.Y);

            SafeSetFormula(line, "LineColor", ColorHelper.ToVisioColorFormula("black", "black"));
            SafeSetFormula(line, "LineWeight", "0.018 in");

            if (dashed)
                SafeSetFormula(line, "LinePattern", "2");

            if (endArrow)
                SafeSetFormula(line, "EndArrow", "4");

            return line;
        }

        private static void ApplyTextStyle(dynamic shape, double fontSizePt)
        {
            SafeSetFormula(shape, "Char.Font", "FONT(\"SimSun\")");
            SafeSetFormula(shape, "Char.Color", "RGB(0,0,0)");
            SafeSetFormula(shape, "Char.Size", fontSizePt.ToString(CultureInfo.InvariantCulture) + " pt");
            SafeSetFormula(shape, "Para.HorzAlign", "1");
            SafeSetFormula(shape, "VerticalAlign", "1");
        }

        private static void ApplyFillAndLine(dynamic shape, string lineColor, string fillColor)
        {
            SafeSetFormula(shape, "LineColor", ColorHelper.ToVisioColorFormula("black", "black"));
            SafeSetFormula(shape, "FillPattern", "1");
            SafeSetFormula(shape, "FillForegnd", ColorHelper.ToVisioColorFormula("#D9D9D9", "#D9D9D9"));
            SafeSetFormula(shape, "LineWeight", "0.018 in");
        }

        private static void SafeSetFormula(dynamic shape, string cellName, string formula)
        {
            object cell = null;
            try
            {
                dynamic dcell = shape.CellsU[cellName];
                cell = dcell;
                dcell.FormulaU = formula;
            }
            catch
            {
            }
            finally
            {
                ReleaseCom(cell);
            }
        }

        private static void ReleaseCom(object obj)
        {
            try
            {
                if (obj != null && Marshal.IsComObject(obj))
                    Marshal.FinalReleaseComObject(obj);
            }
            catch
            {
            }
        }
    }
}
