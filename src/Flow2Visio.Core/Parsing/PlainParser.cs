using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Flow2Visio.Core.Models;
using Flow2Visio.Core.Utils;

namespace Flow2Visio.Core.Parsing
{
    public static class PlainParser
    {
        public static GraphInfo Parse(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("找不到 plain 文件", path);

            var graph = new GraphInfo();
            var lines = File.ReadAllLines(path);

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (string.IsNullOrWhiteSpace(line) || line == "stop")
                    continue;

                var parts = Tokenizer.Tokenize(line);
                if (parts.Count == 0)
                    continue;

                string kind = parts[0];

                if (kind == "graph")
                {
                    ParseGraph(parts, graph);
                }
                else if (kind == "node")
                {
                    graph.Nodes.Add(ParseNode(parts));
                }
                else if (kind == "edge")
                {
                    graph.Edges.Add(ParseEdge(parts));
                }
            }

            return graph;
        }

        private static void ParseGraph(List<string> parts, GraphInfo graph)
        {
            // graph scale width height
            if (parts.Count >= 4)
            {
                graph.Scale = ToDouble(parts[1]);
                graph.Width = ToDouble(parts[2]);
                graph.Height = ToDouble(parts[3]);
            }
        }

        private static NodeInfo ParseNode(List<string> parts)
        {
            return new NodeInfo
            {
                Id = GetPart(parts, 1),
                Cx = ToDouble(GetPart(parts, 2, "0")),
                Cy = ToDouble(GetPart(parts, 3, "0")),
                W = ToDouble(GetPart(parts, 4, "1")),
                H = ToDouble(GetPart(parts, 5, "0.5")),
                Label = GetPart(parts, 6, GetPart(parts, 1)),
                Style = GetPart(parts, 7, string.Empty),
                Shape = GetPart(parts, 8, "box"),
                Color = GetPart(parts, 9, "black"),
                FillColor = GetPart(parts, 10, "white")
            };
        }

        private static EdgeInfo ParseEdge(List<string> parts)
        {
            var edge = new EdgeInfo
            {
                From = StripPort(GetPart(parts, 1)),
                To = StripPort(GetPart(parts, 2))
            };

            int n = int.Parse(GetPart(parts, 3, "0"), CultureInfo.InvariantCulture);
            int baseIndex = 4;

            for (int i = 0; i < n; i++)
            {
                double x = ToDouble(GetPart(parts, baseIndex + 2 * i, "0"));
                double y = ToDouble(GetPart(parts, baseIndex + 2 * i + 1, "0"));
                edge.Points.Add(new Pt(x, y));
            }

            var rest = parts.Skip(baseIndex + 2 * n).ToList();

            if (rest.Count >= 5 &&
                double.TryParse(rest[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lx) &&
                double.TryParse(rest[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double ly))
            {
                edge.Label = rest[0];
                edge.LabelX = lx;
                edge.LabelY = ly;
                edge.Style = rest[3];
                edge.Color = rest[4];
            }
            else if (rest.Count >= 2)
            {
                edge.Style = rest[0];
                edge.Color = rest[1];
            }
            else if (rest.Count == 1)
            {
                edge.Style = rest[0];
            }

            return edge;
        }

        private static string StripPort(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return token;

            int idx = token.IndexOf(':');
            return idx >= 0 ? token.Substring(0, idx) : token;
        }

        private static string GetPart(List<string> parts, int index, string defaultValue = "")
        {
            return index >= 0 && index < parts.Count ? parts[index] : defaultValue;
        }

        private static double ToDouble(string s)
        {
            return double.Parse(s, CultureInfo.InvariantCulture);
        }
    }
}
