using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Graphviz2Visio.Core.Validation
{
    public static class DotRuleValidator
    {
        private static readonly Regex AttributeRegex = new Regex(
            "(?<name>[A-Za-z_][A-Za-z0-9_]*)\\s*=\\s*(?<value>\\\"(?:\\\\.|[^\\\"])*\\\"|[^,\\]\\s]+)",
            RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly Regex NodeIdRegex = new Regex(
            @"^(?<id>[A-Za-z_][A-Za-z0-9_]*)(?::(?<port>[nsew]))?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static ValidationResult ValidateFile(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("找不到 DOT 文件", path);

            return Validate(File.ReadAllText(path));
        }

        public static ValidationResult Validate(string dot)
        {
            if (dot == null)
                throw new ArgumentNullException(nameof(dot));

            var result = new ValidationResult { Layer = "dot" };
            string source = RemoveComments(dot);
            var nodes = ParseNodes(source);
            var edges = ParseEdges(source, result);

            ValidateSplineMode(source, result);
            ValidateLabelsAndPorts(nodes, edges, result);
            ValidateDecisionBranches(nodes, edges, result);
            ValidateOrdinaryNodeFanOut(nodes, edges, result);
            ValidateConstraintFalseEdges(edges, result);
            ValidateSameRankGroups(source, nodes, edges, result);

            return result;
        }

        private static Dictionary<string, DotNode> ParseNodes(string source)
        {
            var nodes = new Dictionary<string, DotNode>(StringComparer.Ordinal);
            int order = 0;

            foreach (string rawStatement in SplitStatements(source))
            {
                string statement = TrimStatementPrefix(rawStatement);
                if (statement.Contains("->") || !statement.Contains("["))
                    continue;

                int bracket = statement.IndexOf('[');
                string id = statement.Substring(0, bracket).Trim();
                if (!NodeIdRegex.IsMatch(id) || IsReservedId(id))
                    continue;

                var attributes = ParseAttributes(statement.Substring(bracket));
                string shape;
                attributes.TryGetValue("shape", out shape);
                nodes[id] = new DotNode
                {
                    Id = id,
                    Shape = Unquote(shape ?? "box").ToLowerInvariant(),
                    Order = order++
                };
            }

            return nodes;
        }

        private static List<DotEdge> ParseEdges(string source, ValidationResult result)
        {
            var edges = new List<DotEdge>();
            foreach (string rawStatement in SplitStatements(source))
            {
                string statement = TrimStatementPrefix(rawStatement);
                if (!statement.Contains("->"))
                    continue;

                int bracket = FindAttributeBracket(statement);
                string expression = bracket >= 0 ? statement.Substring(0, bracket) : statement;
                string attributeText = bracket >= 0 ? statement.Substring(bracket) : string.Empty;
                var attributes = ParseAttributes(attributeText);
                string[] tokens = expression.Split(new[] { "->" }, StringSplitOptions.RemoveEmptyEntries);
                var endpoints = new List<DotEndpoint>();

                foreach (string token in tokens)
                {
                    string candidate = ExtractEndpointToken(token);
                    Match match = NodeIdRegex.Match(candidate);
                    if (!match.Success)
                    {
                        result.Add(
                            "dot.edge.parse",
                            "无法解析边端点；请使用未加引号的稳定 ID，并将每条边写成独立语句。",
                            edge: statement.Trim());
                        endpoints.Clear();
                        break;
                    }

                    endpoints.Add(new DotEndpoint
                    {
                        Id = match.Groups["id"].Value,
                        Port = match.Groups["port"].Success
                            ? match.Groups["port"].Value.ToLowerInvariant()
                            : null
                    });
                }

                for (int index = 0; index + 1 < endpoints.Count; index++)
                {
                    edges.Add(new DotEdge
                    {
                        From = endpoints[index].Id,
                        To = endpoints[index + 1].Id,
                        TailPort = NormalizePort(GetAttribute(attributes, "tailport") ?? endpoints[index].Port),
                        HeadPort = NormalizePort(GetAttribute(attributes, "headport") ?? endpoints[index + 1].Port),
                        Label = Unquote(GetAttribute(attributes, "label") ?? string.Empty),
                        Style = Unquote(GetAttribute(attributes, "style") ?? string.Empty),
                        ConstraintFalse = string.Equals(
                            Unquote(GetAttribute(attributes, "constraint") ?? string.Empty),
                            "false",
                            StringComparison.OrdinalIgnoreCase)
                    });
                }
            }

            return edges;
        }

        private static void ValidateSplineMode(string source, ValidationResult result)
        {
            Match match = Regex.Match(
                source,
                "\\bsplines\\s*=\\s*(?<value>\\\"[^\\\"]*\\\"|[A-Za-z]+)",
                RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                result.Add("dot.splines.missing", "必须显式设置 graph 的 splines=polyline。");
                return;
            }

            string value = Unquote(match.Groups["value"].Value);
            if (!string.Equals(value, "polyline", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(
                    "dot.splines.invalid",
                    "当前验证闭环要求 splines=polyline；不要使用会忽略端口或边标签的 ortho。", edge: value);
            }
        }

        private static void ValidateLabelsAndPorts(
            IDictionary<string, DotNode> nodes,
            IEnumerable<DotEdge> edges,
            ValidationResult result)
        {
            foreach (DotEdge edge in edges)
            {
                string edgeName = EdgeName(edge);
                if (!string.IsNullOrWhiteSpace(edge.Label) && edge.Label != "Y" && edge.Label != "N")
                {
                    result.Add(
                        "dot.edge.label",
                        "连线文字只能是 Y 或 N。",
                        edge: edgeName);
                }

                DotNode source;
                bool decisionEdge = nodes.TryGetValue(edge.From, out source) && source.Shape == "diamond";
                bool auxiliary = edge.ConstraintFalse || ContainsStyle(edge.Style, "dashed");

                if (decisionEdge)
                    continue;

                if (!auxiliary &&
                    (!string.Equals(edge.TailPort, "s", StringComparison.OrdinalIgnoreCase) ||
                     !string.Equals(edge.HeadPort, "n", StringComparison.OrdinalIgnoreCase)))
                {
                    result.Add(
                        "dot.main.ports",
                        "普通主流程边必须使用 tailport=s, headport=n。",
                        edge: edgeName);
                }
            }
        }

        private static void ValidateDecisionBranches(
            IDictionary<string, DotNode> nodes,
            IList<DotEdge> edges,
            ValidationResult result)
        {
            foreach (DotNode node in nodes.Values.Where(n => n.Shape == "diamond"))
            {
                List<DotEdge> outgoing = edges.Where(e => e.From == node.Id).ToList();
                int yesCount = outgoing.Count(e => e.Label == "Y");
                int noCount = outgoing.Count(e => e.Label == "N");

                if (outgoing.Count != 2 || yesCount != 1 || noCount != 1)
                {
                    result.Add(
                        "dot.decision.branches",
                        "判断节点必须恰好有一条 Y 和一条 N 输出。",
                        node: node.Id);
                }

                int downward = outgoing.Count(e => string.Equals(e.TailPort, "s", StringComparison.OrdinalIgnoreCase));
                int sideways = outgoing.Count(e => e.TailPort == "e" || e.TailPort == "w");
                if (downward != 1 || sideways != 1)
                {
                    result.Add(
                        "dot.decision.ports",
                        "判断节点必须有一个向下输出和一个左右侧输出。",
                        node: node.Id);
                }

                foreach (DotEdge outgoingEdge in outgoing)
                {
                    bool validPorts =
                        (outgoingEdge.TailPort == "s" && outgoingEdge.HeadPort == "n") ||
                        (outgoingEdge.TailPort == "e" && outgoingEdge.HeadPort == "w") ||
                        (outgoingEdge.TailPort == "w" && outgoingEdge.HeadPort == "e");
                    if (!validPorts)
                    {
                        result.Add(
                            "dot.decision.branch-ports",
                            "判断分支必须使用 s→n、e→w 或 w→e 的成对端口。",
                            node: node.Id,
                            edge: EdgeName(outgoingEdge));
                    }
                }

                foreach (DotEdge incoming in edges.Where(e => e.To == node.Id))
                {
                    if (!string.Equals(incoming.HeadPort, "n", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(
                            "dot.decision.input-port",
                            "进入判断节点的边必须使用 headport=n。",
                            node: node.Id,
                            edge: EdgeName(incoming));
                    }
                }
            }
        }

        private static void ValidateOrdinaryNodeFanOut(
            IDictionary<string, DotNode> nodes,
            IList<DotEdge> edges,
            ValidationResult result)
        {
            foreach (DotNode node in nodes.Values.Where(n => n.Shape != "diamond"))
            {
                int businessOutputs = edges.Count(e =>
                    e.From == node.Id && !e.ConstraintFalse && !ContainsStyle(e.Style, "dashed"));
                if (businessOutputs > 1)
                {
                    result.Add(
                        "dot.node.fanout",
                        "普通处理节点只能有一个业务出口；请改成判断链或拆分页面。",
                        node: node.Id);
                }
            }
        }

        private static void ValidateConstraintFalseEdges(IList<DotEdge> edges, ValidationResult result)
        {
            foreach (DotEdge edge in edges.Where(e => e.ConstraintFalse))
            {
                if (!HasPath(edge.To, edge.From, edges.Where(e => !e.ConstraintFalse)))
                {
                    result.Add(
                        "dot.constraint-false.not-loop",
                        "constraint=false 只能用于能闭合现有路径的循环返回边。",
                        edge: EdgeName(edge));
                }

                bool sameSide = (edge.TailPort == "e" && edge.HeadPort == "e") ||
                                (edge.TailPort == "w" && edge.HeadPort == "w");
                if (!sameSide)
                {
                    result.Add(
                        "dot.loop.ports",
                        "循环返回边必须从同一侧进出（e→e 或 w→w）。",
                        edge: EdgeName(edge));
                }
            }
        }

        private static void ValidateSameRankGroups(
            string source,
            IDictionary<string, DotNode> nodes,
            IList<DotEdge> edges,
            ValidationResult result)
        {
            MatchCollection groups = Regex.Matches(
                source,
                @"\{(?<body>[^{}]*\brank\s*=\s*same\b[^{}]*)\}",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            foreach (Match group in groups)
            {
                var members = new List<string>();
                foreach (string part in group.Groups["body"].Value.Split(';'))
                {
                    string candidate = part.Trim();
                    if (candidate.StartsWith("rank", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (NodeIdRegex.IsMatch(candidate) && nodes.ContainsKey(candidate))
                        members.Add(candidate);
                }

                bool valid = false;
                if (members.Count == 2)
                {
                    DotNode decision = nodes[members[0]].Shape == "diamond"
                        ? nodes[members[0]]
                        : nodes[members[1]].Shape == "diamond" ? nodes[members[1]] : null;
                    string sideNode = decision == null
                        ? null
                        : members.FirstOrDefault(id => id != decision.Id);
                    valid = decision != null && edges.Any(e =>
                        e.From == decision.Id && e.To == sideNode && (e.TailPort == "e" || e.TailPort == "w"));
                }

                if (!valid)
                {
                    result.Add(
                        "dot.rank-same.invalid",
                        "rank=same 只能包含一个判断节点和它的一个直接侧分支节点。",
                        edge: string.Join(",", members));
                }
            }
        }

        private static bool HasPath(string start, string target, IEnumerable<DotEdge> edges)
        {
            var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (DotEdge edge in edges)
            {
                List<string> targets;
                if (!adjacency.TryGetValue(edge.From, out targets))
                {
                    targets = new List<string>();
                    adjacency[edge.From] = targets;
                }
                targets.Add(edge.To);
            }

            var queue = new Queue<string>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            queue.Enqueue(start);
            visited.Add(start);
            while (queue.Count > 0)
            {
                string current = queue.Dequeue();
                if (current == target)
                    return true;

                List<string> targets;
                if (!adjacency.TryGetValue(current, out targets))
                    continue;
                foreach (string next in targets)
                {
                    if (visited.Add(next))
                        queue.Enqueue(next);
                }
            }
            return false;
        }

        private static IEnumerable<string> SplitStatements(string source)
        {
            var current = new StringBuilder();
            bool inQuote = false;
            bool escaped = false;
            foreach (char ch in source)
            {
                if (inQuote)
                {
                    current.Append(ch);
                    if (escaped)
                        escaped = false;
                    else if (ch == '\\')
                        escaped = true;
                    else if (ch == '"')
                        inQuote = false;
                    continue;
                }

                if (ch == '"')
                {
                    inQuote = true;
                    current.Append(ch);
                }
                else if (ch == ';')
                {
                    yield return current.ToString();
                    current.Clear();
                }
                else
                {
                    current.Append(ch);
                }
            }

            if (current.Length > 0)
                yield return current.ToString();
        }

        private static string RemoveComments(string source)
        {
            var output = new StringBuilder(source.Length);
            bool inQuote = false;
            bool escaped = false;
            bool lineComment = false;
            bool blockComment = false;

            for (int index = 0; index < source.Length; index++)
            {
                char ch = source[index];
                char next = index + 1 < source.Length ? source[index + 1] : '\0';

                if (lineComment)
                {
                    if (ch == '\n')
                    {
                        lineComment = false;
                        output.Append(ch);
                    }
                    continue;
                }
                if (blockComment)
                {
                    if (ch == '*' && next == '/')
                    {
                        blockComment = false;
                        index++;
                    }
                    continue;
                }
                if (!inQuote && ch == '/' && next == '/')
                {
                    lineComment = true;
                    index++;
                    continue;
                }
                if (!inQuote && ch == '/' && next == '*')
                {
                    blockComment = true;
                    index++;
                    continue;
                }

                output.Append(ch);
                if (inQuote)
                {
                    if (escaped)
                        escaped = false;
                    else if (ch == '\\')
                        escaped = true;
                    else if (ch == '"')
                        inQuote = false;
                }
                else if (ch == '"')
                {
                    inQuote = true;
                }
            }

            return output.ToString();
        }

        private static Dictionary<string, string> ParseAttributes(string text)
        {
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in AttributeRegex.Matches(text ?? string.Empty))
                attributes[match.Groups["name"].Value] = match.Groups["value"].Value;
            return attributes;
        }

        private static string GetAttribute(IDictionary<string, string> attributes, string name)
        {
            string value;
            return attributes.TryGetValue(name, out value) ? Unquote(value) : null;
        }

        private static string ExtractEndpointToken(string text)
        {
            string candidate = text.Trim().Trim('{', '}').Trim();
            int newline = Math.Max(candidate.LastIndexOf('\n'), candidate.LastIndexOf('\r'));
            if (newline >= 0)
                candidate = candidate.Substring(newline + 1).Trim();
            string[] words = candidate.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return words.Length == 0 ? string.Empty : words[words.Length - 1].Trim();
        }

        private static string TrimStatementPrefix(string statement)
        {
            string value = statement.Trim().TrimStart('{').Trim();
            value = Regex.Replace(
                value,
                @"^(?:strict\s+)?digraph\s+[A-Za-z_][A-Za-z0-9_]*\s*\{\s*",
                string.Empty,
                RegexOptions.IgnoreCase);
            value = Regex.Replace(
                value,
                @"^subgraph(?:\s+[A-Za-z_][A-Za-z0-9_]*)?\s*\{\s*",
                string.Empty,
                RegexOptions.IgnoreCase);
            return value.Trim();
        }

        private static int FindAttributeBracket(string statement)
        {
            bool inQuote = false;
            bool escaped = false;
            for (int index = 0; index < statement.Length; index++)
            {
                char ch = statement[index];
                if (inQuote)
                {
                    if (escaped)
                        escaped = false;
                    else if (ch == '\\')
                        escaped = true;
                    else if (ch == '"')
                        inQuote = false;
                }
                else if (ch == '"')
                {
                    inQuote = true;
                }
                else if (ch == '[')
                {
                    return index;
                }
            }
            return -1;
        }

        private static bool ContainsStyle(string style, string value)
        {
            return (style ?? string.Empty).Split(',').Any(part =>
                string.Equals(part.Trim(), value, StringComparison.OrdinalIgnoreCase));
        }

        private static string Unquote(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                return value.Substring(1, value.Length - 2).Replace("\\\"", "\"");
            return value;
        }

        private static string NormalizePort(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
        }

        private static bool IsReservedId(string id)
        {
            return string.Equals(id, "graph", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(id, "node", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(id, "edge", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(id, "digraph", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(id, "subgraph", StringComparison.OrdinalIgnoreCase);
        }

        private static string EdgeName(DotEdge edge)
        {
            return edge.From + " -> " + edge.To;
        }

        private sealed class DotNode
        {
            public string Id { get; set; }
            public string Shape { get; set; }
            public int Order { get; set; }
        }

        private sealed class DotEndpoint
        {
            public string Id { get; set; }
            public string Port { get; set; }
        }

        private sealed class DotEdge
        {
            public string From { get; set; }
            public string To { get; set; }
            public string TailPort { get; set; }
            public string HeadPort { get; set; }
            public string Label { get; set; }
            public string Style { get; set; }
            public bool ConstraintFalse { get; set; }
        }
    }
}
