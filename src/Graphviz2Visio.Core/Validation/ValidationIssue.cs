namespace Graphviz2Visio.Core.Validation
{
    public class ValidationIssue
    {
        public string Layer { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
        public string Node { get; set; }
        public string Edge { get; set; }
        public string OtherEdge { get; set; }
        public double? X { get; set; }
        public double? Y { get; set; }
    }
}
