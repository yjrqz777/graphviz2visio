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

                var nodesById = new Dictionary<string, NodeInfo>(StringComparer.Ordinal);
                foreach (var node in graph.Nodes)
                    nodesById[node.Id] = node;

                foreach (var edge in graph.Edges)
                    DrawEdge(dpage, edge, nodesById, offsetX, offsetY);

                foreach (var node in graph.Nodes)
                    DrawNode(dpage, node, offsetX, offsetY);

                foreach (var edge in graph.Edges)
                    DrawEdgeLabel(dpage, edge, offsetX, offsetY);

                ddoc.SaveAs(Path.GetFullPath(outputVsdxPath));
                ddoc.Close();
                dapp.Quit();
            }
            finally
            {
                ReleaseCom(page);
                ReleaseCom(doc);
                ReleaseCom(app);
            }
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

        private static void DrawNode(dynamic page, NodeInfo node, double ox, double oy)
        {
            string shapeType = (node.Shape ?? "box").ToLowerInvariant();

            if (shapeType == "ellipse")
                DrawEllipseNode(page, node, ox, oy);
            else if (shapeType == "diamond")
                DrawDiamondNode(page, node, ox, oy);
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

        private static void DrawDiamondNode(dynamic page, NodeInfo node, double ox, double oy)
        {
            double cx = ox + node.Cx;
            double cy = oy + node.Cy;
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

            if (isLine)
            {
                Pt p1 = AttachToNodeBoundary(fromNode, rawPts[0], rawPts[rawPts.Count - 1]);
                Pt p2 = AttachToNodeBoundary(toNode, rawPts[rawPts.Count - 1], rawPts[0]);
                DrawAndReleaseLine(page, OffsetPoint(p1, ox, oy), OffsetPoint(p2, ox, oy), edge.Color, dashed, true);
                return;
            }

            var pts = BezierHelper.SplineToPolyline(rawPts, 24);
            if (pts.Count < 2)
                return;

            pts[0] = AttachToNodeBoundary(fromNode, pts[0], pts[1]);
            pts[pts.Count - 1] = AttachToNodeBoundary(toNode, pts[pts.Count - 1], pts[pts.Count - 2]);

            for (int i = 0; i < pts.Count - 1; i++)
            {
                DrawAndReleaseLine(
                    page,
                    OffsetPoint(pts[i], ox, oy),
                    OffsetPoint(pts[i + 1], ox, oy),
                    edge.Color,
                    dashed,
                    i == pts.Count - 2);
            }
        }

        private static void DrawAndReleaseLine(dynamic page, Pt p1, Pt p2, string color, bool dashed, bool endArrow)
        {
            var line = DrawSimpleLine(page, p1, p2, color, dashed, endArrow);
            ReleaseCom(line);
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

        private static void DrawEdgeLabel(dynamic page, EdgeInfo edge, double ox, double oy)
        {
            if (string.IsNullOrWhiteSpace(edge.Label))
                return;

            double cx = ox + edge.LabelX;
            double cy = oy + edge.LabelY;
            double w = Math.Max(0.35, 0.16 * edge.Label.Length);
            double h = 0.22;

            var box = page.DrawRectangle(cx - w / 2.0, cy - h / 2.0, cx + w / 2.0, cy + h / 2.0);
            box.Text = edge.Label;

            SafeSetFormula(box, "LinePattern", "0");
            SafeSetFormula(box, "FillPattern", "1");
            SafeSetFormula(box, "FillForegnd", "RGB(255,255,255)");
            ApplyTextStyle(box, 8.0);

            ReleaseCom(box);
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
