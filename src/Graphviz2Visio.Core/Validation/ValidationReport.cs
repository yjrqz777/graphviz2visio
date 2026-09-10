namespace Graphviz2Visio.Core.Validation
{
    public class ValidationReport
    {
        public ValidationResult Dot { get; set; }
        public ValidationResult Layout { get; set; }
        public bool Passed => (Dot == null || Dot.Passed) && (Layout == null || Layout.Passed);
    }
}
