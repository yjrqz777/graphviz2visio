using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using Graphviz2Visio.Core.Models;
using Graphviz2Visio.Core.Validation;

namespace Graphviz2Visio.Vsdx.Validation
{
    public static class VsdxPackageValidator
    {
        private const double Epsilon = 0.001;
        private const int MaxIssues = 200;

        private static readonly XNamespace RelationshipNamespace =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        private static readonly XNamespace PackageRelationshipNamespace =
            "http://schemas.openxmlformats.org/package/2006/relationships";

        private static readonly XNamespace ContentTypeNamespace =
            "http://schemas.openxmlformats.org/package/2006/content-types";

        public static ValidationResult ValidateFile(string path)
        {
            return ValidateFile(path, null, null);
        }

        public static ValidationResult ValidateFile(
            string path,
            IList<string> expectedPageNames,
            IList<int> expectedEdgeCounts)
        {
            var result = new ValidationResult { Layer = "vsdx" };
            if (!File.Exists(path))
            {
                result.Add("vsdx.file.missing", "VSDX 文件不存在。");
                return result;
            }

            try
            {
                using ZipArchive archive = ZipFile.OpenRead(path);
                ValidateArchive(archive, expectedPageNames, expectedEdgeCounts, result);
            }
            catch (InvalidDataException ex)
            {
                result.Add("vsdx.package.invalid", "VSDX 压缩包无效: " + ex.Message);
            }
            catch (Exception ex)
            {
                result.Add("vsdx.read.failed", "无法读取 VSDX: " + ex.Message);
            }
            return result;
        }

        private static void ValidateArchive(
            ZipArchive archive,
            IList<string> expectedPageNames,
            IList<int> expectedEdgeCounts,
            ValidationResult result)
        {
            string[] requiredParts =
            {
                "[Content_Types].xml",
                "_rels/.rels",
                "visio/document.xml",
                "visio/_rels/document.xml.rels",
                "visio/pages/pages.xml",
                "visio/pages/_rels/pages.xml.rels"
            };

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (!names.Add(entry.FullName))
                    result.Add("vsdx.entry.duplicate", "压缩包包含重复条目: " + entry.FullName);
            }

            foreach (string requiredPart in requiredParts)
            {
                if (!names.Contains(requiredPart))
                    result.Add("vsdx.part.missing", "缺少必需部件: " + requiredPart);
            }
            if (result.Issues.Count > 0)
                return;

            XDocument pagesDocument = LoadXml(archive, "visio/pages/pages.xml", result);
            XDocument relationshipsDocument = LoadXml(
                archive,
                "visio/pages/_rels/pages.xml.rels",
                result);
            XDocument contentTypesDocument = LoadXml(archive, "[Content_Types].xml", result);
            if (pagesDocument == null || relationshipsDocument == null || contentTypesDocument == null)
                return;

            XNamespace visio = pagesDocument.Root.Name.Namespace;
            List<XElement> pageElements = pagesDocument.Root.Elements(visio + "Page").ToList();
            Dictionary<string, string> pageTargets = relationshipsDocument.Root
                .Elements(PackageRelationshipNamespace + "Relationship")
                .ToDictionary(
                    element => (string)element.Attribute("Id") ?? string.Empty,
                    element => (string)element.Attribute("Target") ?? string.Empty,
                    StringComparer.Ordinal);

            if (expectedPageNames != null && pageElements.Count != expectedPageNames.Count)
            {
                result.Add(
                    "vsdx.page.count",
                    "页面数量不正确，期望 " + expectedPageNames.Count +
                    "，实际 " + pageElements.Count + "。");
            }

            var pageIds = new HashSet<string>(StringComparer.Ordinal);
            var pageNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < pageElements.Count; index++)
            {
                XElement page = pageElements[index];
                string pageId = (string)page.Attribute("ID") ?? string.Empty;
                string pageName = (string)page.Attribute("Name") ?? string.Empty;
                if (!pageIds.Add(pageId))
                    result.Add("vsdx.page.id.duplicate", "页面 ID 重复: " + pageId);
                if (!pageNames.Add(pageName))
                    result.Add("vsdx.page.name.duplicate", "页面名称重复: " + pageName);

                if (expectedPageNames != null && index < expectedPageNames.Count &&
                    !string.Equals(pageName, expectedPageNames[index], StringComparison.Ordinal))
                {
                    result.Add(
                        "vsdx.page.name",
                        "页面名称不正确，期望 '" + expectedPageNames[index] +
                        "'，实际 '" + pageName + "'。");
                }

                XElement relation = page.Element(visio + "Rel");
                string relationId = relation == null
                    ? string.Empty
                    : (string)relation.Attribute(RelationshipNamespace + "id") ?? string.Empty;
                string target;
                if (!pageTargets.TryGetValue(relationId, out target))
                {
                    result.Add(
                        "vsdx.page.relationship",
                        "页面缺少有效关系: " + pageName);
                    continue;
                }

                string pagePart = "visio/pages/" + target.Replace('\\', '/');
                if (!names.Contains(pagePart))
                {
                    result.Add(
                        "vsdx.page.part.missing",
                        "页面 XML 不存在: " + pagePart);
                    continue;
                }

                int? expectedEdges = expectedEdgeCounts != null && index < expectedEdgeCounts.Count
                    ? expectedEdgeCounts[index]
                    : (int?)null;
                ValidatePage(archive, pagePart, pageName, expectedEdges, result);
                if (result.Issues.Count >= MaxIssues)
                    return;
            }

            ValidatePageContentTypes(contentTypesDocument, pageElements.Count, result);
        }

        private static void ValidatePage(
            ZipArchive archive,
            string pagePart,
            string pageName,
            int? expectedEdgeCount,
            ValidationResult result)
        {
            XDocument document = LoadXml(archive, pagePart, result);
            if (document == null)
                return;

            XNamespace visio = document.Root.Name.Namespace;
            List<XElement> shapes = document
                .Descendants(visio + "Shape")
                .ToList();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (XElement shape in shapes)
            {
                string id = (string)shape.Attribute("ID") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(id) || !ids.Add(id))
                {
                    result.Add(
                        "vsdx.shape.id",
                        "页面 '" + pageName + "' 包含空白或重复的 Shape ID: " + id);
                }
            }

            List<XElement> edges = shapes
                .Where(shape => ((string)shape.Attribute("NameU") ?? string.Empty)
                    .StartsWith("Edge_", StringComparison.Ordinal))
                .ToList();
            if (expectedEdgeCount.HasValue && edges.Count != expectedEdgeCount.Value)
            {
                result.Add(
                    "vsdx.edge.count",
                    "页面 '" + pageName + "' 的逻辑边数量不正确，期望 " +
                    expectedEdgeCount.Value + "，实际 " + edges.Count + "。");
            }

            foreach (XElement edge in edges)
            {
                ValidateEdgeGeometry(visio, edge, pageName, result);
                if (result.Issues.Count >= MaxIssues)
                    return;
            }
        }

        private static void ValidateEdgeGeometry(
            XNamespace visio,
            XElement edge,
            string pageName,
            ValidationResult result)
        {
            string edgeName = (string)edge.Attribute("NameU") ?? string.Empty;
            double beginX;
            double beginY;
            double endX;
            double endY;
            if (!TryGetCell(edge, visio, "BeginX", out beginX) ||
                !TryGetCell(edge, visio, "BeginY", out beginY) ||
                !TryGetCell(edge, visio, "EndX", out endX) ||
                !TryGetCell(edge, visio, "EndY", out endY))
            {
                result.Add(
                    "vsdx.edge.not-1d",
                    "页面 '" + pageName + "' 的边不是有效一维 Shape。",
                    edge: edgeName);
                return;
            }

            XElement geometry = edge.Elements(visio + "Section")
                .FirstOrDefault(section =>
                    string.Equals((string)section.Attribute("N"), "Geometry", StringComparison.Ordinal));
            if (geometry == null)
            {
                result.Add(
                    "vsdx.edge.geometry.missing",
                    "边缺少 Geometry Section。",
                    edge: edgeName);
                return;
            }

            List<XElement> rows = geometry.Elements(visio + "Row").ToList();
            if (rows.Count < 2 ||
                !string.Equals((string)rows[0].Attribute("T"), "MoveTo", StringComparison.Ordinal))
            {
                result.Add(
                    "vsdx.edge.geometry.start",
                    "边必须以一个 MoveTo 开始并至少包含一个 LineTo。",
                    edge: edgeName);
                return;
            }

            if (rows.Skip(1).Any(row =>
                !string.Equals((string)row.Attribute("T"), "LineTo", StringComparison.Ordinal)))
            {
                result.Add(
                    "vsdx.edge.geometry.curve",
                    "边 Geometry 中只能包含 MoveTo 和 LineTo。",
                    edge: edgeName);
                return;
            }

            double angle = Math.Atan2(endY - beginY, endX - beginX);
            double cos = Math.Cos(angle);
            double sin = Math.Sin(angle);
            var points = new List<Pt>();
            foreach (XElement row in rows)
            {
                double localX;
                double localY;
                if (!TryGetCell(row, visio, "X", out localX) ||
                    !TryGetCell(row, visio, "Y", out localY))
                {
                    result.Add(
                        "vsdx.edge.geometry.coordinate",
                        "边 Geometry 包含无效坐标。",
                        edge: edgeName);
                    return;
                }
                points.Add(new Pt(
                    beginX + localX * cos - localY * sin,
                    beginY + localX * sin + localY * cos));
            }

            if (!SamePoint(points[0], new Pt(beginX, beginY)) ||
                !SamePoint(points[points.Count - 1], new Pt(endX, endY)))
            {
                result.Add(
                    "vsdx.edge.geometry.endpoints",
                    "边 Geometry 的首尾点与一维 Shape 端点不一致。",
                    edge: edgeName);
            }

            for (int index = 0; index + 1 < points.Count; index++)
            {
                bool horizontal = Math.Abs(points[index].Y - points[index + 1].Y) < Epsilon;
                bool vertical = Math.Abs(points[index].X - points[index + 1].X) < Epsilon;
                if (horizontal && vertical)
                {
                    result.Add(
                        "vsdx.edge.zero-length",
                        "生成的边包含零长度线段。",
                        edge: edgeName);
                    break;
                }
                if (!horizontal && !vertical)
                {
                    result.Add(
                        "vsdx.edge.non-orthogonal",
                        "生成的边包含非水平、非垂直线段。",
                        edge: edgeName,
                        x: Math.Round(points[index + 1].X, 4),
                        y: Math.Round(points[index + 1].Y, 4));
                    break;
                }
            }

            double endArrow;
            if (!TryGetCell(edge, visio, "EndArrow", out endArrow) || endArrow < Epsilon)
            {
                result.Add(
                    "vsdx.edge.arrow.missing",
                    "边缺少结束箭头。",
                    edge: edgeName);
            }
        }

        private static bool SamePoint(Pt left, Pt right)
        {
            return Math.Abs(left.X - right.X) < Epsilon &&
                Math.Abs(left.Y - right.Y) < Epsilon;
        }

        private static void ValidatePageContentTypes(
            XDocument document,
            int pageCount,
            ValidationResult result)
        {
            var parts = new HashSet<string>(
                document.Root.Elements(ContentTypeNamespace + "Override")
                    .Where(element => string.Equals(
                        (string)element.Attribute("ContentType"),
                        "application/vnd.ms-visio.page+xml",
                        StringComparison.Ordinal))
                    .Select(element => (string)element.Attribute("PartName") ?? string.Empty),
                StringComparer.Ordinal);

            for (int index = 0; index < pageCount; index++)
            {
                string expected = "/visio/pages/page" +
                    (index + 1).ToString(CultureInfo.InvariantCulture) + ".xml";
                if (!parts.Contains(expected))
                    result.Add("vsdx.content-type.page", "页面缺少 Content Type: " + expected);
            }
        }

        private static bool TryGetCell(
            XElement parent,
            XNamespace visio,
            string name,
            out double value)
        {
            value = 0;
            XElement cell = parent.Elements(visio + "Cell")
                .FirstOrDefault(element => string.Equals(
                    (string)element.Attribute("N"),
                    name,
                    StringComparison.Ordinal));
            return cell != null && double.TryParse(
                (string)cell.Attribute("V"),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value);
        }

        private static XDocument LoadXml(
            ZipArchive archive,
            string path,
            ValidationResult result)
        {
            ZipArchiveEntry entry = archive.GetEntry(path);
            if (entry == null)
            {
                result.Add("vsdx.part.missing", "缺少 XML 部件: " + path);
                return null;
            }

            try
            {
                using Stream stream = entry.Open();
                return XDocument.Load(stream);
            }
            catch (Exception ex)
            {
                result.Add("vsdx.xml.invalid", "XML 无效: " + path + ": " + ex.Message);
                return null;
            }
        }
    }
}
