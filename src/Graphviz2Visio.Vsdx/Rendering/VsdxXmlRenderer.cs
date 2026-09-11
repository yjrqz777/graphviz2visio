using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Graphviz2Visio.Core.Models;
using Graphviz2Visio.Core.Parsing;
using Graphviz2Visio.Core.Utils;
using Graphviz2Visio.Core.Validation;
using Graphviz2Visio.Vsdx.Validation;

namespace Graphviz2Visio.Vsdx.Rendering
{
    public static class VsdxXmlRenderer
    {
        private const string TemplateResourceName =
            "Graphviz2Visio.Vsdx.Templates.base-easy.vsdx";

        private const string PageRelationshipType =
            "http://schemas.microsoft.com/visio/2010/relationships/page";

        private static readonly XNamespace RelationshipNamespace =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        private static readonly XNamespace PackageRelationshipNamespace =
            "http://schemas.openxmlformats.org/package/2006/relationships";

        private static readonly XNamespace ContentTypeNamespace =
            "http://schemas.openxmlformats.org/package/2006/content-types";

        public static void RenderPlainToVsdx(
            string plainPath,
            string outputVsdxPath)
        {
            RenderPlainFilesToVsdx(
                new[] { plainPath },
                outputVsdxPath,
                null);
        }

        public static void RenderPlainFilesToVsdx(
            IEnumerable<string> plainPaths,
            string outputVsdxPath,
            IEnumerable<string> requestedPageNames)
        {
            if (plainPaths == null)
                throw new ArgumentNullException(nameof(plainPaths));
            if (string.IsNullOrWhiteSpace(outputVsdxPath))
                throw new ArgumentException("Output path cannot be empty.", nameof(outputVsdxPath));

            List<string> paths = plainPaths.ToList();
            if (paths.Count == 0)
                throw new ArgumentException("At least one Plain file is required.", nameof(plainPaths));

            var pageNames = requestedPageNames == null
                ? paths.Select(Path.GetFileNameWithoutExtension).ToList()
                : requestedPageNames.Select(value => value == null ? null : value.Trim()).ToList();

            if (pageNames.Count != paths.Count)
                throw new ArgumentException("One page name is required for every Plain file.", nameof(requestedPageNames));
            ValidatePageNames(pageNames);

            var pages = new List<PreparedPage>();
            for (int index = 0; index < paths.Count; index++)
            {
                if (!File.Exists(paths[index]))
                    throw new FileNotFoundException("Plain file not found.", paths[index]);

                GraphInfo graph = PlainParser.Parse(paths[index]);
                RoutedLayout routed = RoutingPipeline.Prepare(graph);
                if (!routed.Passed)
                {
                    string details = string.Join(
                        "; ",
                        routed.Validation.Issues.Take(5).Select(issue =>
                            issue.Code + ": " + issue.Message));
                    throw new InvalidOperationException(
                        "Orthogonal routing validation failed for page '" +
                        pageNames[index] + "': " + details);
                }

                pages.Add(PreparePage(graph, routed.Routes, pageNames[index]));
            }

            WritePackage(outputVsdxPath, pages);
        }

        private static void WritePackage(
            string outputVsdxPath,
            IList<PreparedPage> pages)
        {
            string fullOutputPath = Path.GetFullPath(outputVsdxPath);
            string outputDirectory = Path.GetDirectoryName(fullOutputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            string temporaryPath = Path.Combine(
                outputDirectory ?? Directory.GetCurrentDirectory(),
                "." + Path.GetFileName(fullOutputPath) + "." +
                Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                CopyEmbeddedTemplate(temporaryPath);
                using (ZipArchive archive = ZipFile.Open(temporaryPath, ZipArchiveMode.Update))
                    PopulatePackage(archive, pages);

                ValidationResult validation = VsdxPackageValidator.ValidateFile(
                    temporaryPath,
                    pages.Select(page => page.PageName).ToList(),
                    pages.Select(page => page.Graph.Edges.Count).ToList());
                if (!validation.Passed)
                {
                    string details = string.Join(
                        "; ",
                        validation.Issues.Take(8).Select(issue =>
                            issue.Code + ": " + issue.Message));
                    throw new InvalidDataException("Generated VSDX validation failed: " + details);
                }

                File.Move(temporaryPath, fullOutputPath, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }

        private static void CopyEmbeddedTemplate(string destinationPath)
        {
            Assembly assembly = typeof(VsdxXmlRenderer).Assembly;
            using Stream template = assembly.GetManifestResourceStream(TemplateResourceName);
            if (template == null)
            {
                throw new InvalidOperationException(
                    "Embedded VSDX template was not found: " + TemplateResourceName);
            }

            using FileStream destination = File.Create(destinationPath);
            template.CopyTo(destination);
        }

        private static void PopulatePackage(
            ZipArchive archive,
            IList<PreparedPage> pages)
        {
            XDocument originalPages = LoadXml(archive, "visio/pages/pages.xml");
            XNamespace visio = originalPages.Root.Name.Namespace;
            XElement templatePage = originalPages.Root.Elements(visio + "Page").FirstOrDefault();
            if (templatePage == null)
                throw new InvalidDataException("The VSDX template has no Page element.");

            foreach (ZipArchiveEntry entry in archive.Entries
                .Where(entry => Regex.IsMatch(
                    entry.FullName,
                    @"^visio/pages/page\d+\.xml$",
                    RegexOptions.CultureInvariant))
                .ToList())
            {
                entry.Delete();
            }

            foreach (ZipArchiveEntry entry in archive.Entries
                .Where(entry => Regex.IsMatch(
                    entry.FullName,
                    @"^visio/pages/_rels/page\d+\.xml\.rels$",
                    RegexOptions.CultureInvariant))
                .ToList())
            {
                entry.Delete();
            }

            var generatedPageDocuments = new List<XDocument>();
            for (int index = 0; index < pages.Count; index++)
            {
                generatedPageDocuments.Add(BuildPageDocument(pages[index], visio));
                ReplaceXml(
                    archive,
                    "visio/pages/page" + (index + 1).ToString(CultureInfo.InvariantCulture) + ".xml",
                    generatedPageDocuments[index]);
            }

            ReplaceXml(
                archive,
                "visio/pages/pages.xml",
                BuildPagesDocument(originalPages, templatePage, pages, visio));
            ReplaceXml(
                archive,
                "visio/pages/_rels/pages.xml.rels",
                BuildPagesRelationships(pages.Count));

            XDocument contentTypes = LoadXml(archive, "[Content_Types].xml");
            UpdateContentTypes(contentTypes, pages.Count);
            ReplaceXml(archive, "[Content_Types].xml", contentTypes);

            UpdateApplicationProperties(archive, pages);
            UpdateCoreProperties(archive);
            UpdateWindows(archive, pages[0]);
        }

        private static XDocument BuildPagesDocument(
            XDocument original,
            XElement templatePage,
            IList<PreparedPage> pages,
            XNamespace visio)
        {
            XElement root = new XElement(original.Root);
            root.RemoveNodes();

            for (int index = 0; index < pages.Count; index++)
            {
                PreparedPage pageInfo = pages[index];
                XElement page = new XElement(templatePage);
                page.SetAttributeValue("ID", index.ToString(CultureInfo.InvariantCulture));
                page.SetAttributeValue("NameU", "Page-" + (index + 1).ToString(CultureInfo.InvariantCulture));
                page.SetAttributeValue("Name", pageInfo.PageName);
                page.SetAttributeValue("ViewScale", "-1");
                page.SetAttributeValue("ViewCenterX", Format(pageInfo.PageWidth / 2.0));
                page.SetAttributeValue("ViewCenterY", Format(pageInfo.PageHeight / 2.0));

                XElement pageSheet = page.Element(visio + "PageSheet");
                if (pageSheet == null)
                {
                    pageSheet = new XElement(visio + "PageSheet");
                    page.AddFirst(pageSheet);
                }
                SetCell(pageSheet, visio, "PageWidth", pageInfo.PageWidth);
                SetCell(pageSheet, visio, "PageHeight", pageInfo.PageHeight);
                SetCell(pageSheet, visio, "DrawingSizeType", 0);
                SetCell(pageSheet, visio, "DrawingResizeType", 0);

                page.Elements(visio + "Rel").Remove();
                page.Add(new XElement(
                    visio + "Rel",
                    new XAttribute(
                        RelationshipNamespace + "id",
                        "rId" + (index + 1).ToString(CultureInfo.InvariantCulture))));
                root.Add(page);
            }

            return new XDocument(original.Declaration, root);
        }

        private static XDocument BuildPagesRelationships(int pageCount)
        {
            var root = new XElement(PackageRelationshipNamespace + "Relationships");
            for (int index = 0; index < pageCount; index++)
            {
                root.Add(new XElement(
                    PackageRelationshipNamespace + "Relationship",
                    new XAttribute("Id", "rId" + (index + 1).ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("Type", PageRelationshipType),
                    new XAttribute("Target", "page" + (index + 1).ToString(CultureInfo.InvariantCulture) + ".xml")));
            }
            return NewDocument(root);
        }

        private static XDocument BuildPageDocument(
            PreparedPage page,
            XNamespace visio)
        {
            var root = new XElement(
                visio + "PageContents",
                new XAttribute(XNamespace.Xml + "space", "preserve"));
            var shapes = new XElement(visio + "Shapes");
            root.Add(shapes);

            int shapeId = 1;
            for (int index = 0; index < page.Graph.Edges.Count; index++)
            {
                EdgeInfo edge = page.Graph.Edges[index];
                List<Pt> route = page.Routes[edge]
                    .Select(point => Offset(point, page.OffsetX, page.OffsetY))
                    .ToList();
                shapes.Add(BuildEdgeShape(visio, shapeId++, index + 1, edge, route));
            }

            for (int index = 0; index < page.Graph.Edges.Count; index++)
            {
                EdgeInfo edge = page.Graph.Edges[index];
                if (string.IsNullOrWhiteSpace(edge.Label))
                    continue;

                List<Pt> route = page.Routes[edge]
                    .Select(point => Offset(point, page.OffsetX, page.OffsetY))
                    .ToList();
                Pt labelPosition = FindLabelPosition(
                    edge,
                    route,
                    page.OffsetX,
                    page.OffsetY,
                    page.NodesById);
                shapes.Add(BuildLabelShape(
                    visio,
                    shapeId++,
                    index + 1,
                    labelPosition,
                    edge.Label.Trim()));
            }

            for (int index = 0; index < page.Graph.Nodes.Count; index++)
            {
                NodeInfo node = page.Graph.Nodes[index];
                shapes.Add(BuildNodeShape(
                    visio,
                    shapeId++,
                    index + 1,
                    node,
                    page.OffsetX,
                    page.OffsetY));
            }

            return NewDocument(root);
        }

        private static XElement BuildEdgeShape(
            XNamespace visio,
            int shapeId,
            int edgeIndex,
            EdgeInfo edge,
            IList<Pt> route)
        {
            if (route.Count < 2)
                throw new InvalidDataException("A routed edge must contain at least two points.");

            Pt begin = route[0];
            Pt end = route[route.Count - 1];
            double dx = end.X - begin.X;
            double dy = end.Y - begin.Y;
            double angle = Math.Atan2(dy, dx);
            double width = Math.Sqrt(dx * dx + dy * dy);
            if (width < 0.000001)
                width = 0.000001;

            var shape = new XElement(
                visio + "Shape",
                new XAttribute("ID", shapeId),
                new XAttribute("NameU", "Edge_" + edgeIndex.ToString("D4", CultureInfo.InvariantCulture)),
                new XAttribute("Name", "Edge_" + edgeIndex.ToString("D4", CultureInfo.InvariantCulture)),
                new XAttribute("Type", "Shape"),
                new XAttribute("LineStyle", "8"),
                new XAttribute("FillStyle", "8"),
                new XAttribute("TextStyle", "8"));

            shape.Add(
                Cell(visio, "PinX", (begin.X + end.X) / 2.0),
                Cell(visio, "PinY", (begin.Y + end.Y) / 2.0),
                Cell(visio, "Width", width),
                Cell(visio, "Height", 0),
                Cell(visio, "LocPinX", width / 2.0),
                Cell(visio, "LocPinY", 0),
                Cell(visio, "Angle", angle),
                Cell(visio, "BeginX", begin.X),
                Cell(visio, "BeginY", begin.Y),
                Cell(visio, "EndX", end.X),
                Cell(visio, "EndY", end.Y),
                Cell(visio, "LineColor", "#000000"),
                Cell(visio, "LineWeight", 0.018, "IN"),
                Cell(visio, "LinePattern",
                    string.Equals(edge.Style, "dashed", StringComparison.OrdinalIgnoreCase) ? "2" : "1"),
                Cell(visio, "BeginArrow", "0"),
                Cell(visio, "EndArrow", "4"),
                Cell(visio, "BeginArrowSize", "2"),
                Cell(visio, "EndArrowSize", "2"));

            var geometry = new XElement(
                visio + "Section",
                new XAttribute("N", "Geometry"),
                new XAttribute("IX", "0"),
                Cell(visio, "NoFill", "1"),
                Cell(visio, "NoLine", "0"),
                Cell(visio, "NoShow", "0"));

            double cos = Math.Cos(angle);
            double sin = Math.Sin(angle);
            for (int index = 0; index < route.Count; index++)
            {
                double pointDx = route[index].X - begin.X;
                double pointDy = route[index].Y - begin.Y;
                double localX = pointDx * cos + pointDy * sin;
                double localY = -pointDx * sin + pointDy * cos;
                geometry.Add(new XElement(
                    visio + "Row",
                    new XAttribute("T", index == 0 ? "MoveTo" : "LineTo"),
                    new XAttribute("IX", index + 1),
                    Cell(visio, "X", localX),
                    Cell(visio, "Y", localY)));
            }
            shape.Add(geometry);
            return shape;
        }

        private static XElement BuildNodeShape(
            XNamespace visio,
            int shapeId,
            int nodeIndex,
            NodeInfo node,
            double offsetX,
            double offsetY)
        {
            double centerX = node.Cx + offsetX;
            double centerY = node.Cy + offsetY;
            double width = Math.Max(node.W, 0.01);
            double height = Math.Max(node.H, 0.01);
            string shapeType = (node.Shape ?? "box").ToLowerInvariant();

            var shape = new XElement(
                visio + "Shape",
                new XAttribute("ID", shapeId),
                new XAttribute("NameU", "Node_" + nodeIndex.ToString("D4", CultureInfo.InvariantCulture)),
                new XAttribute("Name", "Node_" + nodeIndex.ToString("D4", CultureInfo.InvariantCulture)),
                new XAttribute("Type", "Shape"),
                new XAttribute("LineStyle", "7"),
                new XAttribute("FillStyle", "7"),
                new XAttribute("TextStyle", "7"));

            shape.Add(
                Cell(visio, "PinX", centerX),
                Cell(visio, "PinY", centerY),
                Cell(visio, "Width", width),
                Cell(visio, "Height", height),
                Cell(visio, "LocPinX", width / 2.0),
                Cell(visio, "LocPinY", height / 2.0),
                Cell(visio, "Angle", 0),
                Cell(visio, "LineColor", "#000000"),
                Cell(visio, "LineWeight", 0.018, "IN"),
                Cell(visio, "FillForegnd", "#D9D9D9"),
                Cell(visio, "FillPattern", "1"),
                Cell(visio, "VerticalAlign", "1"));

            if (shapeType == "ellipse" || shapeType == "oval")
                shape.Add(Cell(visio, "Rounding", height / 2.0, "IN"));
            else if ((node.Style ?? string.Empty).IndexOf("rounded", StringComparison.OrdinalIgnoreCase) >= 0)
                shape.Add(Cell(visio, "Rounding", 0.08, "IN"));

            shape.Add(TextFormatting(visio, 10.0));
            shape.Add(ParagraphFormatting(visio));
            shape.Add(shapeType == "diamond"
                ? DiamondGeometry(visio, width, height)
                : RectangleGeometry(visio, width, height));
            shape.Add(new XElement(visio + "Text", NormalizeText(node.Label ?? node.Id)));
            return shape;
        }

        private static XElement BuildLabelShape(
            XNamespace visio,
            int shapeId,
            int edgeIndex,
            Pt position,
            string text)
        {
            const double width = 0.32;
            const double height = 0.18;
            var shape = new XElement(
                visio + "Shape",
                new XAttribute("ID", shapeId),
                new XAttribute("NameU", "EdgeLabel_" + edgeIndex.ToString("D4", CultureInfo.InvariantCulture)),
                new XAttribute("Name", "EdgeLabel_" + edgeIndex.ToString("D4", CultureInfo.InvariantCulture)),
                new XAttribute("Type", "Shape"),
                new XAttribute("LineStyle", "0"),
                new XAttribute("FillStyle", "0"),
                new XAttribute("TextStyle", "7"));

            shape.Add(
                Cell(visio, "PinX", position.X),
                Cell(visio, "PinY", position.Y),
                Cell(visio, "Width", width),
                Cell(visio, "Height", height),
                Cell(visio, "LocPinX", width / 2.0),
                Cell(visio, "LocPinY", height / 2.0),
                Cell(visio, "Angle", 0),
                Cell(visio, "LinePattern", "0"),
                Cell(visio, "FillForegnd", "#FFFFFF"),
                Cell(visio, "FillPattern", "1"),
                Cell(visio, "VerticalAlign", "1"));
            shape.Add(TextFormatting(visio, 8.0));
            shape.Add(ParagraphFormatting(visio));
            shape.Add(RectangleGeometry(visio, width, height));
            shape.Add(new XElement(visio + "Text", NormalizeText(text)));
            return shape;
        }

        private static XElement RectangleGeometry(
            XNamespace visio,
            double width,
            double height)
        {
            return new XElement(
                visio + "Section",
                new XAttribute("N", "Geometry"),
                new XAttribute("IX", "0"),
                Row(visio, "MoveTo", 1, 0, 0),
                Row(visio, "LineTo", 2, width, 0),
                Row(visio, "LineTo", 3, width, height),
                Row(visio, "LineTo", 4, 0, height),
                Row(visio, "LineTo", 5, 0, 0));
        }

        private static XElement DiamondGeometry(
            XNamespace visio,
            double width,
            double height)
        {
            return new XElement(
                visio + "Section",
                new XAttribute("N", "Geometry"),
                new XAttribute("IX", "0"),
                Row(visio, "MoveTo", 1, width / 2.0, height),
                Row(visio, "LineTo", 2, width, height / 2.0),
                Row(visio, "LineTo", 3, width / 2.0, 0),
                Row(visio, "LineTo", 4, 0, height / 2.0),
                Row(visio, "LineTo", 5, width / 2.0, height));
        }

        private static XElement TextFormatting(XNamespace visio, double sizeInPoints)
        {
            return new XElement(
                visio + "Section",
                new XAttribute("N", "Character"),
                new XElement(
                    visio + "Row",
                    new XAttribute("IX", "0"),
                    Cell(visio, "Font", "0"),
                    Cell(visio, "Color", "#000000"),
                    Cell(visio, "Size", sizeInPoints / 72.0, "PT")));
        }

        private static XElement ParagraphFormatting(XNamespace visio)
        {
            return new XElement(
                visio + "Section",
                new XAttribute("N", "Paragraph"),
                new XElement(
                    visio + "Row",
                    new XAttribute("IX", "0"),
                    Cell(visio, "HorzAlign", "1"),
                    Cell(visio, "SpLine", "-1.2")));
        }

        private static XElement Row(
            XNamespace visio,
            string type,
            int index,
            double x,
            double y)
        {
            return new XElement(
                visio + "Row",
                new XAttribute("T", type),
                new XAttribute("IX", index),
                Cell(visio, "X", x),
                Cell(visio, "Y", y));
        }

        private static Pt FindLabelPosition(
            EdgeInfo edge,
            IList<Pt> route,
            double offsetX,
            double offsetY,
            IDictionary<string, NodeInfo> nodesById)
        {
            NodeInfo source;
            nodesById.TryGetValue(edge.From ?? string.Empty, out source);
            bool decision = source != null &&
                string.Equals(source.Shape, "diamond", StringComparison.OrdinalIgnoreCase);

            if (decision && route.Count >= 2)
            {
                Pt port = route[0];
                Pt next = route[1];
                double dx = next.X - port.X;
                double dy = next.Y - port.Y;
                if (Math.Abs(dx) >= Math.Abs(dy))
                    return new Pt(port.X + (dx >= 0 ? 0.24 : -0.24), port.Y + 0.14);
                return new Pt(port.X + 0.14, port.Y + (dy >= 0 ? 0.24 : -0.24));
            }

            double preferredX = edge.LabelX + offsetX;
            double preferredY = edge.LabelY + offsetY;
            int best = 0;
            double bestDistance = double.PositiveInfinity;
            for (int index = 0; index + 1 < route.Count; index++)
            {
                double middleX = (route[index].X + route[index + 1].X) / 2.0;
                double middleY = (route[index].Y + route[index + 1].Y) / 2.0;
                double dx = middleX - preferredX;
                double dy = middleY - preferredY;
                double distance = dx * dx + dy * dy;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = index;
                }
            }

            Pt a = route[best];
            Pt b = route[best + 1];
            double x = (a.X + b.X) / 2.0;
            double y = (a.Y + b.Y) / 2.0;
            if (Math.Abs(a.Y - b.Y) < 0.001)
                y += 0.13;
            else
                x += 0.13;
            return new Pt(x, y);
        }

        private static PreparedPage PreparePage(
            GraphInfo graph,
            Dictionary<EdgeInfo, List<Pt>> routes,
            string pageName)
        {
            const double margin = 1.0;
            double minX = double.PositiveInfinity;
            double minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity;
            double maxY = double.NegativeInfinity;

            foreach (NodeInfo node in graph.Nodes)
            {
                minX = Math.Min(minX, node.Cx - node.W / 2.0);
                minY = Math.Min(minY, node.Cy - node.H / 2.0);
                maxX = Math.Max(maxX, node.Cx + node.W / 2.0);
                maxY = Math.Max(maxY, node.Cy + node.H / 2.0);
            }

            foreach (List<Pt> route in routes.Values)
            {
                foreach (Pt point in route)
                {
                    minX = Math.Min(minX, point.X);
                    minY = Math.Min(minY, point.Y);
                    maxX = Math.Max(maxX, point.X);
                    maxY = Math.Max(maxY, point.Y);
                }
            }

            if (double.IsInfinity(minX))
            {
                minX = 0;
                minY = 0;
                maxX = 1;
                maxY = 1;
            }

            double contentWidth = maxX - minX;
            double contentHeight = maxY - minY;
            double pageWidth = Math.Max(8.27, contentWidth + margin * 2.0);
            double pageHeight = Math.Max(11.69, contentHeight + margin * 2.0);
            double offsetX = (pageWidth - contentWidth) / 2.0 - minX;
            double offsetY = (pageHeight - contentHeight) / 2.0 - minY;

            var nodesById = new Dictionary<string, NodeInfo>(StringComparer.Ordinal);
            foreach (NodeInfo node in graph.Nodes)
                nodesById[node.Id] = node;

            return new PreparedPage
            {
                Graph = graph,
                Routes = routes,
                NodesById = nodesById,
                PageName = pageName,
                PageWidth = pageWidth,
                PageHeight = pageHeight,
                OffsetX = offsetX,
                OffsetY = offsetY
            };
        }

        private static void UpdateContentTypes(XDocument document, int pageCount)
        {
            document.Root.Elements(ContentTypeNamespace + "Override")
                .Where(element => Regex.IsMatch(
                    (string)element.Attribute("PartName") ?? string.Empty,
                    @"^/visio/pages/page\d+\.xml$",
                    RegexOptions.CultureInvariant))
                .Remove();

            for (int index = 0; index < pageCount; index++)
            {
                document.Root.Add(new XElement(
                    ContentTypeNamespace + "Override",
                    new XAttribute("PartName", "/visio/pages/page" +
                        (index + 1).ToString(CultureInfo.InvariantCulture) + ".xml"),
                    new XAttribute("ContentType", "application/vnd.ms-visio.page+xml")));
            }
        }

        private static void UpdateApplicationProperties(
            ZipArchive archive,
            IList<PreparedPage> pages)
        {
            const string path = "docProps/app.xml";
            ZipArchiveEntry entry = archive.GetEntry(path);
            if (entry == null)
                return;

            XDocument document = LoadXml(archive, path);
            XNamespace properties = document.Root.Name.Namespace;
            XNamespace variant = "http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes";

            XElement template = document.Root.Element(properties + "Template");
            if (template != null)
                template.Value = string.Empty;

            XElement headingPairs = document.Root.Element(properties + "HeadingPairs");
            if (headingPairs != null)
            {
                headingPairs.ReplaceNodes(new XElement(
                    variant + "vector",
                    new XAttribute("size", "2"),
                    new XAttribute("baseType", "variant"),
                    new XElement(variant + "variant", new XElement(variant + "lpstr", "页")),
                    new XElement(variant + "variant", new XElement(variant + "i4", pages.Count))));
            }

            XElement titles = document.Root.Element(properties + "TitlesOfParts");
            if (titles != null)
            {
                titles.ReplaceNodes(new XElement(
                    variant + "vector",
                    new XAttribute("size", pages.Count),
                    new XAttribute("baseType", "lpstr"),
                    pages.Select(page => new XElement(variant + "lpstr", page.PageName))));
            }
            ReplaceXml(archive, path, document);
        }

        private static void UpdateCoreProperties(ZipArchive archive)
        {
            const string path = "docProps/core.xml";
            ZipArchiveEntry entry = archive.GetEntry(path);
            if (entry == null)
                return;

            XDocument document = LoadXml(archive, path);
            XNamespace core = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
            XNamespace dc = "http://purl.org/dc/elements/1.1/";
            XNamespace dcterms = "http://purl.org/dc/terms/";

            XElement creator = document.Root.Element(dc + "creator");
            if (creator != null)
                creator.Value = string.Empty;
            document.Root.Element(core + "lastPrinted")?.Remove();

            XElement modified = document.Root.Element(dcterms + "modified");
            if (modified != null)
                modified.Value = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            ReplaceXml(archive, path, document);
        }

        private static void UpdateWindows(
            ZipArchive archive,
            PreparedPage firstPage)
        {
            const string path = "visio/windows.xml";
            ZipArchiveEntry entry = archive.GetEntry(path);
            if (entry == null)
                return;

            XDocument document = LoadXml(archive, path);
            List<XElement> windows = document.Descendants()
                .Where(element => element.Name.LocalName == "Window")
                .ToList();
            foreach (XElement window in windows
                .Where(element => !string.Equals(
                    (string)element.Attribute("WindowType"),
                    "Drawing",
                    StringComparison.Ordinal)))
                window.Remove();

            XElement drawingWindow = windows.FirstOrDefault(element => string.Equals(
                (string)element.Attribute("WindowType"),
                "Drawing",
                StringComparison.Ordinal));
            if (drawingWindow != null)
            {
                drawingWindow.SetAttributeValue("Page", "0");
                drawingWindow.SetAttributeValue("ViewScale", "-1");
                drawingWindow.SetAttributeValue("ViewCenterX", Format(firstPage.PageWidth / 2.0));
                drawingWindow.SetAttributeValue("ViewCenterY", Format(firstPage.PageHeight / 2.0));
            }
            ReplaceXml(archive, path, document);
        }

        private static void ValidatePageNames(IList<string> pageNames)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string pageName in pageNames)
            {
                if (string.IsNullOrWhiteSpace(pageName))
                    throw new ArgumentException("Page name cannot be empty.");
                if (pageName.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
                    throw new ArgumentException("Page name contains an invalid character: " + pageName);
                if (!names.Add(pageName))
                    throw new ArgumentException("Page names must be unique: " + pageName);
            }
        }

        private static void SetCell(
            XElement parent,
            XNamespace visio,
            string name,
            double value)
        {
            XElement cell = parent.Elements(visio + "Cell")
                .FirstOrDefault(element => string.Equals(
                    (string)element.Attribute("N"),
                    name,
                    StringComparison.Ordinal));
            if (cell == null)
            {
                cell = new XElement(visio + "Cell", new XAttribute("N", name));
                parent.Add(cell);
            }
            cell.SetAttributeValue("V", Format(value));
        }

        private static XElement Cell(
            XNamespace visio,
            string name,
            double value,
            string unit = null)
        {
            return Cell(visio, name, Format(value), unit);
        }

        private static XElement Cell(
            XNamespace visio,
            string name,
            string value,
            string unit = null)
        {
            var cell = new XElement(
                visio + "Cell",
                new XAttribute("N", name),
                new XAttribute("V", value));
            if (!string.IsNullOrWhiteSpace(unit))
                cell.SetAttributeValue("U", unit);
            return cell;
        }

        private static XDocument LoadXml(ZipArchive archive, string path)
        {
            ZipArchiveEntry entry = archive.GetEntry(path);
            if (entry == null)
                throw new InvalidDataException("VSDX part is missing: " + path);
            using Stream stream = entry.Open();
            return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }

        private static void ReplaceXml(
            ZipArchive archive,
            string path,
            XDocument document)
        {
            archive.GetEntry(path)?.Delete();
            ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.Optimal);
            using Stream stream = entry.Open();
            using XmlWriter writer = XmlWriter.Create(stream, new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                Indent = false,
                OmitXmlDeclaration = false,
                CloseOutput = false
            });
            document.Save(writer);
        }

        private static XDocument NewDocument(XElement root)
        {
            return new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                root);
        }

        private static Pt Offset(Pt point, double offsetX, double offsetY)
        {
            return new Pt(point.X + offsetX, point.Y + offsetY);
        }

        private static string NormalizeText(string text)
        {
            return (text ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace("\r", "\n");
        }

        private static string Format(double value)
        {
            return value.ToString("0.###############", CultureInfo.InvariantCulture);
        }

        private sealed class PreparedPage
        {
            public GraphInfo Graph;
            public Dictionary<EdgeInfo, List<Pt>> Routes;
            public Dictionary<string, NodeInfo> NodesById;
            public string PageName;
            public double PageWidth;
            public double PageHeight;
            public double OffsetX;
            public double OffsetY;
        }
    }
}
