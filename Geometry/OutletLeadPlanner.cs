using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Media3D;

namespace FCUAutoDesign
{
    internal static class OutletLeadPlanner
    {
        // 包围盒仅用于建议提前转弯长度，不能代替 Revit 管件和实体校验。
        // allowance 是候选搜索余量，不是工程净距或管件尺寸承诺。
        public static double[] BeforeObstacles(Point3D start, Vector3D outward,
            IEnumerable<Point3D[]> obstacleCorners, double requestedLead,
            double radius, double allowance, double minLength, double? minimumStraightLength = null)
        {
            if (!Valid(start) || !Finite(outward.X) || !Finite(outward.Y) || !Finite(outward.Z)
                || outward.Length < 1e-9 || Math.Abs(outward.Z) > 1e-6
                || !Finite(requestedLead) || requestedLead <= 0 || !Finite(radius) || radius <= 0
                || !Finite(allowance) || allowance <= 0 || !Finite(minLength) || minLength <= 0
                || obstacleCorners == null
                || (minimumStraightLength.HasValue && (!Finite(minimumStraightLength.Value) || minimumStraightLength.Value <= 0)))
                throw new InvalidOperationException("设备出管避障输入无效。");
            outward.Z = 0; outward.Normalize();
            Vector3D side = new Vector3D(-outward.Y, outward.X, 0);
            double nearest = double.PositiveInfinity;
            foreach (Point3D[] corners in obstacleCorners)
            {
                if (corners == null || corners.Length != 8 || corners.Any(p => !Valid(p)))
                    throw new InvalidOperationException("障碍物包围盒无效。");
                var local = corners.Select(p => p - start).ToArray();
                double minX = local.Min(p => Vector3D.DotProduct(p, outward));
                double maxX = local.Max(p => Vector3D.DotProduct(p, outward));
                double minY = local.Min(p => Vector3D.DotProduct(p, side));
                double maxY = local.Max(p => Vector3D.DotProduct(p, side));
                double minZ = local.Min(p => p.Z), maxZ = local.Max(p => p.Z);
                double envelope = radius + allowance;
                if (maxX + radius < 0 || minX - envelope > requestedLead
                    || minY > envelope || maxY < -envelope || minZ > envelope || maxZ < -envelope)
                    continue;
                // 邻近接口的大包围盒可能覆盖起点：它不能提供正的转弯长度，
                // 但也不能抹掉其他障碍提供的候选。实际碰撞由后续检查处理。
                double available = minX - radius - allowance;
                if (available > minLength) nearest = Math.Min(nearest, available);
            }
            if (double.IsPositiveInfinity(nearest) || nearest <= minLength) return new double[0];
            double limit = Math.Min(nearest, requestedLead);
            return new[] { limit, limit * .75, limit * .5 }
                .Where(x => x > minLength && x < requestedLead - minLength
                    && (!minimumStraightLength.HasValue || x > minimumStraightLength.Value)).Distinct().ToArray();
        }

        private static bool Finite(double x) { return !double.IsNaN(x) && !double.IsInfinity(x); }
        private static bool Valid(Point3D p) { return Finite(p.X) && Finite(p.Y) && Finite(p.Z); }
    }
}
