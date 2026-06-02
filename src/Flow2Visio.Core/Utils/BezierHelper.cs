using System.Collections.Generic;
using Flow2Visio.Core.Models;

namespace Flow2Visio.Core.Utils
{
    public static class BezierHelper
    {
        public static List<Pt> SplineToPolyline(IList<Pt> points, int samplesPerSegment = 24)
        {
            if (points == null || points.Count < 2)
                return new List<Pt>();

            if (points.Count < 4)
                return new List<Pt>(points);

            // Graphviz plain 常见是 3n+1 个点，表示 cubic bezier 链
            if ((points.Count - 1) % 3 != 0)
                return new List<Pt>(points);

            var result = new List<Pt>();

            for (int i = 0; i < points.Count - 1; i += 3)
            {
                Pt p0 = points[i];
                Pt p1 = points[i + 1];
                Pt p2 = points[i + 2];
                Pt p3 = points[i + 3];

                for (int s = 0; s <= samplesPerSegment; s++)
                {
                    if (i > 0 && s == 0)
                        continue;

                    double t = (double)s / samplesPerSegment;
                    result.Add(BezierPoint(p0, p1, p2, p3, t));
                }
            }

            return result;
        }

        private static Pt BezierPoint(Pt p0, Pt p1, Pt p2, Pt p3, double t)
        {
            double u = 1.0 - t;

            double x =
                u * u * u * p0.X +
                3 * u * u * t * p1.X +
                3 * u * t * t * p2.X +
                t * t * t * p3.X;

            double y =
                u * u * u * p0.Y +
                3 * u * u * t * p1.Y +
                3 * u * t * t * p2.Y +
                t * t * t * p3.Y;

            return new Pt(x, y);
        }
    }
}
