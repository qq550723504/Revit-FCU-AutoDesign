using System;
using System.Windows.Media.Media3D;

namespace FCUAutoDesign
{
    internal static class MainPipeSegmentLocator
    {
        public static int Locate(Point3D[] starts, Point3D[] ends, Point3D approach, double tolerance)
        {
            if (starts == null || ends == null || starts.Length != ends.Length
                || !Valid(approach) || !Finite(tolerance) || tolerance <= 0)
                throw new InvalidOperationException("主管定位参数无效。");
            int match = -1;
            for (int i = 0; i < starts.Length; i++)
            {
                if (!Valid(starts[i]) || !Valid(ends[i]))
                    throw new InvalidOperationException("主管段坐标无效。");
                Vector3D axis = ends[i] - starts[i];
                double xy = axis.X * axis.X + axis.Y * axis.Y;
                if (xy < 1e-12) continue;
                double t = ((approach.X - starts[i].X) * axis.X
                    + (approach.Y - starts[i].Y) * axis.Y) / xy;
                if (t <= 0 || t >= 1) continue;
                Point3D join = starts[i] + axis * t;
                if ((join - starts[i]).Length <= tolerance || (join - ends[i]).Length <= tolerance) continue;
                if (match >= 0) throw new InvalidOperationException("接入位置对应多个主管段，不能唯一定位。");
                match = i;
            }
            if (match < 0)
                throw new InvalidOperationException("接入位置不在可用主管段内，可能超出所选主管、位于已安装三通处或过于靠近端点。");
            return match;
        }

        private static bool Finite(double x) { return !double.IsNaN(x) && !double.IsInfinity(x); }
        private static bool Valid(Point3D p) { return Finite(p.X) && Finite(p.Y) && Finite(p.Z); }
    }
}
