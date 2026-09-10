using System;
using System.Collections.Generic;
using Graphviz2Visio.Core.Models;
using Graphviz2Visio.Core.Validation;

namespace Graphviz2Visio.Core.Utils
{
    public static class RoutingPipeline
    {
        public static RoutedLayout Prepare(GraphInfo graph)
        {
            if (graph == null)
                throw new ArgumentNullException(nameof(graph));

            ValidationResult detected = LayoutValidator.ValidateRaw(graph);
            Dictionary<EdgeInfo, List<Pt>> routes = OrthogonalRouter.Route(graph);
            ValidationResult validation = LayoutValidator.Validate(graph, routes);
            return new RoutedLayout(detected, validation, routes);
        }
    }

    public sealed class RoutedLayout
    {
        public RoutedLayout(
            ValidationResult detected,
            ValidationResult validation,
            Dictionary<EdgeInfo, List<Pt>> routes)
        {
            Detected = detected;
            Validation = validation;
            Routes = routes;
        }

        public ValidationResult Detected { get; }
        public ValidationResult Validation { get; }
        public Dictionary<EdgeInfo, List<Pt>> Routes { get; }
        public bool Passed => Validation != null && Validation.Passed;
    }
}
