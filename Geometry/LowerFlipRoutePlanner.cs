using System;
using System.Collections.Generic;
using System.Windows.Media.Media3D;

namespace FCUAutoDesign
{
    // 当前供回水/冷凝水接管实际调用的几何计算；长度单位由调用方统一。
    internal static class LowerFlipRoutePlanner
    {
        public static Point3D[] Approach(Point3D start, Vector3D outward,
            double lead, double drop, double lateral, double minLength)
        {
            if (!Valid(start) || !Finite(outward.X) || !Finite(outward.Y) || !Finite(outward.Z)
                || outward.Length < 1e-9 || Math.Abs(outward.Z) > 1e-6)
                throw new InvalidOperationException("接口必须具有有效的水平出管方向。");
            if (!Finite(minLength) || minLength <= 0 || !Finite(lead) || lead <= minLength
                || !Finite(drop) || drop <= minLength || !Finite(lateral)
                || (lateral != 0 && Math.Abs(lateral) <= minLength))
                throw new InvalidOperationException("路径长度无效或过短。");

            outward.Z = 0;
            outward.Normalize();
            Point3D leadEnd = start + outward * lead;
            var points = new List<Point3D> { start, leadEnd };
            if (lateral != 0)
            {
                // 横移必须在下翻之前，避免横移与主管接近段共线或折返。
                leadEnd += new Vector3D(-outward.Y, outward.X, 0) * lateral;
                points.Add(leadEnd);
            }
            points.Add(leadEnd - new Vector3D(0, 0, drop));
            return points.ToArray();
        }

        public static Point3D[] Complete(Point3D[] approach, Point3D mainJoin, double minLength)
        {
            if (approach == null || approach.Length < 3 || !Valid(mainJoin))
                throw new InvalidOperationException("接管路径或主管接入点无效。");
            var points = new List<Point3D>(approach);
            points.Add(new Point3D(mainJoin.X, mainJoin.Y, approach[approach.Length - 1].Z));
            points.Add(mainJoin);
            Validate(points.ToArray(), minLength);
            return points.ToArray();
        }

        public static void Validate(Point3D[] points, double minLength)
        {
            if (points == null || points.Length < 2 || !Finite(minLength) || minLength <= 0)
                throw new InvalidOperationException("接管路径无效。");
            for (int i = 0; i < points.Length; i++)
            {
                if (!Valid(points[i])) throw new InvalidOperationException("接管路径坐标无效。");
                if (i == 0) continue;
                Vector3D segment = points[i] - points[i - 1];
                if (segment.Length <= minLength)
                    throw new InvalidOperationException("固定路径产生零长度或过短管段，请调整路径尺寸或主管位置。");
                if (i < 2) continue;
                Vector3D previous = points[i - 1] - points[i - 2];
                previous.Normalize(); segment.Normalize();
                if (Math.Abs(Vector3D.DotProduct(previous, segment)) > 1e-6)
                    throw new InvalidOperationException("固定路径第 " + (i - 1)
                        + " 个转折不是直角（可能共线或折返），不能创建直角弯头。");
            }
        }

        private static bool Finite(double x) { return !double.IsNaN(x) && !double.IsInfinity(x); }
        private static bool Valid(Point3D p) { return Finite(p.X) && Finite(p.Y) && Finite(p.Z); }
    }
}
