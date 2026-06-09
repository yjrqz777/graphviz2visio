using System;
using System.Collections.Generic;
using Graphviz2Visio.Core.Models;

namespace Graphviz2Visio.Core.Utils
{
    public static class BezierHelper
    {
        /// <summary>
        /// 将 Graphviz plain 的 cubic 贝塞尔控制点链（3n+1 个点）转换为折线点序列。
        /// 整条曲线在全局参数 [0, n] 上均匀采样，输出折线段数恰好为 maxSegments，
        /// 即返回 maxSegments + 1 个点。
        /// </summary>
        /// <param name="points">控制点序列；长度应满足 (count-1) % 3 == 0。</param>
        /// <param name="maxSegments">整条曲线最多允许的直线段数，默认 3。</param>
        public static List<Pt> SplineToPolyline(IList<Pt> points, int maxSegments = 3)
        {
            if (points == null || points.Count < 2)
                return new List<Pt>();

            if (points.Count < 4)
                return new List<Pt>(points);

            // Graphviz plain 常见是 3n+1 个点，表示 cubic bezier 链
            if ((points.Count - 1) % 3 != 0)
                return new List<Pt>(points);

            if (maxSegments < 1)
                maxSegments = 1;

            int cubicCount = (points.Count - 1) / 3;
            var result = new List<Pt>(maxSegments + 1);

            for (int i = 0; i <= maxSegments; i++)
            {
                // 将全局采样参数 u 均匀分布在 [0, cubicCount] 区间。
                double u = (double)i / maxSegments * cubicCount;

                int segIdx = (int)Math.Floor(u);
                if (segIdx >= cubicCount)
                    segIdx = cubicCount - 1;

                double t = u - segIdx;
                int baseIdx = segIdx * 3;

                result.Add(BezierPoint(
                    points[baseIdx],
                    points[baseIdx + 1],
                    points[baseIdx + 2],
                    points[baseIdx + 3],
                    t));
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
