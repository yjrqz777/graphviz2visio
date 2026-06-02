using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Flow2Visio.Core.Models;
using Flow2Visio.Core.Parsing;
using Flow2Visio.Core.Utils;

namespace Flow2Visio.Visio.Rendering
{
    public static class VisioRenderer
    {
        public static void RenderPlainToVisio(string plainPath, string outputVsdxPath, bool visible = false)
        {
            if (!File.Exists(plainPath))
                throw new FileNotFoundException("找不到 plain 文件", plainPath);

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
                    throw new InvalidOperationException("未找到 Visio.Application，请确认已安装 Microsoft Visio。");

                dynamic dapp = Activator.CreateInstance(visioType);
                app = dapp;
                dapp.Visible = visible ? 1 : 0;

                dynamic ddoc = dapp.Documents.Add("");
                doc = ddoc;

                dynamic dpage = dapp.ActivePage;
                page = dpage;

                // 注意：这里用 (object)dpage 避免 dynamic 导致 tuple 命名字段丢失
                var offset = PreparePage((object)dpage, graph, 1.0);
                double offsetX = offset.offsetX;
                double offsetY = offset.offsetY;

                foreach (var edge in graph.Edges)
                    DrawEdge(dpage, edge, offsetX, offsetY);

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

        private static void DrawEdge(dynamic page, EdgeInfo edge, double ox, double oy)
        {
            var pts = BezierHelper.SplineToPolyline(edge.Points, 24);
            if (pts == null || pts.Count < 2)
                return;

            object lastLine = null;

            try
            {
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    Pt p1 = new Pt(ox + pts[i].X, oy + pts[i].Y);
                    Pt p2 = new Pt(ox + pts[i + 1].X, oy + pts[i + 1].Y);

                    lastLine = DrawSimpleLine(
                        page,
                        p1,
                        p2,
                        edge.Color,
                        string.Equals(edge.Style, "dashed", StringComparison.OrdinalIgnoreCase),
                        false);
                }

                if (lastLine != null)
                {
                    dynamic dl = lastLine;
                    SafeSetFormula(dl, "EndArrow", "4");
                    SafeSetFormula(dl, "LineWeight", "0.014 in");
                }
            }
            finally
            {
                ReleaseCom(lastLine);
            }
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
