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
            if (!File.Exists(plainPath))
                throw new FileNotFoundException("Plain file not found.", plainPath);

            var graph = PlainParser.Parse(plainPath);

            string outDir = Path.GetDirectoryName(Path.GetFullPath(outputVsdxPath));
            if (!string.IsNullOrWhiteSpace(outDir) && !Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);

            object app = null;
            object doc = null;
            object page = null;
            object flowchartStencil = null;
            object decisionMaster = null;

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

                dynamic dpage = dapp.ActivePage;
                page = dpage;

                // Cast to object so tuple field names survive dynamic binding.
                var offset = PreparePage((object)dpage, graph, 1.0);
                double offsetX = offset.offsetX;
                double offsetY = offset.offsetY;

                // 如果包含菱形（判定）节点，提前打开 Visio 自带的"基本流程图形状"模具，
                // 以便用标准 Decision master 直接落到页面，而不是用 4 条直线拼。
                bool hasDiamond = false;
                foreach (var n in graph.Nodes)
                {
                    if (string.Equals(n.Shape, "diamond", StringComparison.OrdinalIgnoreCase))
                    {
                        hasDiamond = true;
                        break;
                    }
                }

                dynamic dDecisionMaster = null;
                if (hasDiamond)
                {
                    dynamic dStencil = OpenFlowchartStencil(dapp);
                    flowchartStencil = dStencil;
                    dDecisionMaster = FindDecisionMaster(dStencil);
                    decisionMaster = dDecisionMaster;
                }

                var nodesById = new Dictionary<string, NodeInfo>(StringComparer.Ordinal);
                foreach (var node in graph.Nodes)
                    nodesById[node.Id] = node;

                foreach (var edge in graph.Edges)
                    DrawEdge(dpage, edge, nodesById, offsetX, offsetY);

                foreach (var node in graph.Nodes)
                    DrawNode(dpage, node, offsetX, offsetY, dDecisionMaster);

                ddoc.SaveAs(Path.GetFullPath(outputVsdxPath));
                ddoc.Close();
                dapp.Quit();
            }
            finally
            {
                ReleaseCom(decisionMaster);
                if (flowchartStencil != null)
                {
                    try { ((dynamic)flowchartStencil).Close(); } catch { }
                    ReleaseCom(flowchartStencil);
                }
                ReleaseCom(page);
                ReleaseCom(doc);
                ReleaseCom(app);
            }
        }

        /// <summary>
        /// 尝试打开 Visio 内置的"基本流程图形状"模具，失败返回 null。
        /// 兼容多语言/多版本：优先 Application.GetBuiltInStencilFile，再尝试常见文件名。
        /// </summary>
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

        private static (double offsetX, double offsetY) PreparePage(object pageObj, GraphInfo graph, double margin)
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
                foreach (var p in e.Points)
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

            page.PageSheet.CellsU["PageWidth"].ResultIU = pageW;
            page.PageSheet.CellsU["PageHeight"].ResultIU = pageH;

            double offsetX = (pageW - contentW) / 2.0 - minX;
            double offsetY = (pageH - contentH) / 2.0 - minY;

            return (offsetX, offsetY);
        }

        private static void DrawNode(dynamic page, NodeInfo node, double ox, double oy, dynamic decisionMaster)
        {
            string shapeType = (node.Shape ?? "box").ToLowerInvariant();

            if (shapeType == "ellipse")
                DrawEllipseNode(page, node, ox, oy);
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

        private static void DrawEllipseNode(dynamic page, NodeInfo node, double ox, double oy)
        {
            double cx = ox + node.Cx;
            double cy = oy + node.Cy;

            var shape = page.DrawOval(
                cx - node.W / 2.0,
                cy - node.H / 2.0,
                cx + node.W / 2.0,
                cy + node.H / 2.0);

            shape.Text = node.Label ?? node.Id;

            ApplyFillAndLine(shape, node.Color, node.FillColor);
            ApplyTextStyle(shape, 10.0);

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

        private static void DrawEdge(dynamic page, EdgeInfo edge, IDictionary<string, NodeInfo> nodesById, double ox, double oy)
        {
            var rawPts = edge.Points;
            if (rawPts == null || rawPts.Count < 2)
                return;

            bool isLine = IsLineSegment(rawPts);
            NodeInfo fromNode;
            NodeInfo toNode;
            nodesById.TryGetValue(edge.From ?? string.Empty, out fromNode);
            nodesById.TryGetValue(edge.To ?? string.Empty, out toNode);

            bool dashed = string.Equals(edge.Style, "dashed", StringComparison.OrdinalIgnoreCase);
            bool hasLabel = !string.IsNullOrWhiteSpace(edge.Label);

            // 统一成"折线点序列 pts"：直线短路为 2 点，曲线最多 4 点（3 段）。
            List<Pt> pts;
            if (isLine)
            {
                pts = new List<Pt>
                {
                    AttachToNodeBoundary(fromNode, rawPts[0], rawPts[rawPts.Count - 1]),
                    AttachToNodeBoundary(toNode, rawPts[rawPts.Count - 1], rawPts[0])
                };
            }
            else
            {
                pts = BezierHelper.SplineToPolyline(rawPts, 3);
                if (pts.Count < 2)
                    return;
                pts[0] = AttachToNodeBoundary(fromNode, pts[0], pts[1]);
                pts[pts.Count - 1] = AttachToNodeBoundary(toNode, pts[pts.Count - 1], pts[pts.Count - 2]);
            }

            // 让 label 直接作为某一段线条 1D Shape 的 .Text，挑选离 graphviz 给定
            // label 坐标最近的那一段承载（graphviz 与 pts 处于同一坐标系，未加 ox/oy）。
            int labelSegIdx = hasLabel ? FindNearestSegmentIndex(pts, edge.LabelX, edge.LabelY) : -1;

            for (int i = 0; i < pts.Count - 1; i++)
            {
                Pt p1 = OffsetPoint(pts[i], ox, oy);
                Pt p2 = OffsetPoint(pts[i + 1], ox, oy);
                bool endArrow = (i == pts.Count - 2);

                dynamic line = DrawSimpleLine(page, p1, p2, edge.Color, dashed, endArrow);
                try
                {
                    if (i == labelSegIdx)
                        AttachLabelToSegment(line, p1, p2, ox + edge.LabelX, oy + edge.LabelY, edge.Label);
                }
                finally
                {
                    ReleaseCom(line);
                }
            }
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

        /// <summary>
        /// Detects whether all points represent one straight segment.
        /// </summary>
        private static bool IsLineSegment(IList<Pt> points, double tolerance = 0.01)
        {
            if (points == null || points.Count < 3)
                return true;

            // Use the first and last points as the target line.
            Pt p1 = points[0];
            Pt pn = points[points.Count - 1];

            double dx = pn.X - p1.X;
            double dy = pn.Y - p1.Y;
            double lineLen = Math.Sqrt(dx * dx + dy * dy);

            if (lineLen < tolerance)
                return true;

            // 濠碘槅鍋€閸嬫捇鏌＄仦璇插姕濠⒀冪Ч瀵灚寰勬径搴″箑闂傚倸鍊搁顓㈠磻閿濆鍙婃い鏍ㄧ閸庡﹪鏌涢敂鍝勫缂佺粯鐗犲鍫曟晬閸曨剛鍑界紓浣瑰劤閵囨绮?
            for (int i = 1; i < points.Count - 1; i++)
            {
                Pt p = points[i];

                // Perpendicular distance from the point to the target line.
                double dist = Math.Abs((p.Y - p1.Y) * dx - (p.X - p1.X) * dy) / lineLen;

                if (dist > tolerance)
                    return false;
            }

            return true;
        }

        private static object DrawSimpleLine(dynamic page, Pt p1, Pt p2, string color, bool dashed, bool endArrow)
        {
            var line = page.DrawLine(p1.X, p1.Y, p2.X, p2.Y);

            SafeSetFormula(line, "LineColor", ColorHelper.ToVisioColorFormula(color, "black"));
            SafeSetFormula(line, "LineWeight", "0.014 in");

            if (dashed)
                SafeSetFormula(line, "LinePattern", "2");

            if (endArrow)
                SafeSetFormula(line, "EndArrow", "4");

            return line;
        }

        private static void ApplyTextStyle(dynamic shape, double fontSizePt)
        {
            SafeSetFormula(shape, "Char.Size", fontSizePt.ToString(CultureInfo.InvariantCulture) + " pt");
            SafeSetFormula(shape, "Para.HorzAlign", "1");
            SafeSetFormula(shape, "VerticalAlign", "1");
        }

        private static void ApplyFillAndLine(dynamic shape, string lineColor, string fillColor)
        {
            SafeSetFormula(shape, "LineColor", ColorHelper.ToVisioColorFormula(lineColor, "black"));
            SafeSetFormula(shape, "FillPattern", "1");
            SafeSetFormula(shape, "FillForegnd", ColorHelper.ToVisioColorFormula(fillColor, "white"));
            SafeSetFormula(shape, "LineWeight", "0.014 in");
        }

        private static void SafeSetFormula(dynamic shape, string cellName, string formula)
        {
            try
            {
                shape.CellsU[cellName].FormulaU = formula;
            }
            catch
            {
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
