using System.Collections.Generic;

namespace Graphviz2Visio.Core.Validation
{
    public class ValidationResult
    {
        public string Layer { get; set; }
        public List<ValidationIssue> Issues { get; } = new List<ValidationIssue>();
        public bool Passed => Issues.Count == 0;

        public void Add(string code, string message, string node = null, string edge = null,
            string otherEdge = null, double? x = null, double? y = null)
        {
            Issues.Add(new ValidationIssue
            {
                Layer = Layer,
                Code = code,
                Message = message,
                Node = node,
                Edge = edge,
                OtherEdge = otherEdge,
                X = x,
                Y = y
            });
        }
    }
}
