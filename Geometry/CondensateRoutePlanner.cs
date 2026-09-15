using System;
using System.Collections.Generic;
using System.Windows.Media.Media3D;

namespace FCUAutoDesign
{
    // 仅使用 .NET 双精度几何类型，不读取或修改 Revit 模型。长度单位由调用方统一。
    internal static class CondensateRoutePlanner
    {
        public static Point3D[] Plan(Point3D start, Vector3D outward,
            Point3D mainStart, Point3D mainEnd, double leadLength, double minLength, double bendOffset = 0,
            bool orthogonalDetour = false)
        {
            if (!Finite(bendOffset) || !Finite(leadLength) || !Finite(minLength) || minLength <= 0 || leadLength <= minLength)
                throw new InvalidOperationException("冷凝水预留长度无效。");
            if (!Valid(start) || !Valid(mainStart) || !Valid(mainEnd)
                || !Finite(outward.X) || !Finite(outward.Y) || !Finite(outward.Z)
                || outward.Length < 1e-9 || Math.Abs(outward.Z) > 1e-6)
                throw new InvalidOperationException("冷凝水接口必须具有有效的水平出管方向。");
            outward.Normalize();
            Vector3D axis = mainEnd - mainStart;
            double horizontalSquared = axis.X * axis.X + axis.Y * axis.Y;
            if (horizontalSquared <= minLength * minLength)
                throw new InvalidOperationException("当前冷凝水接入支持水平或有坡度的直线主管，不支持立管。");
            Point3D lead = start + outward * leadLength;
            double t = ((lead.X - mainStart.X) * axis.X + (lead.Y - mainStart.Y) * axis.Y) / horizontalSquared;
            Point3D join = mainStart + axis * t;
            if (t <= 0 || t >= 1 || (join - mainStart).Length <= minLength || (join - mainEnd).Length <= minLength)
                throw new InvalidOperationException("冷凝水接入投影超出所选主管或过于靠近端点，请选择覆盖接入位置的管段。");

            Vector3D cross = new Vector3D(join.X - lead.X, join.Y - lead.Y, 0);
            double crossLength = cross.Length;
            if (crossLength <= minLength)
                throw new InvalidOperationException("冷凝水横向接近段过短，无法容纳弯头，请调整设备位置或所选主管。");
            cross.Normalize();
            double alignment = Vector3D.DotProduct(outward, cross);
            if (!orthogonalDetour && alignment < -1 + 1e-6)
                throw new InvalidOperationException("冷凝水路线需要原路折返，请调整设备出管方向或所选主管。");
            if (orthogonalDetour)
            {
                // 保持设备端水平出管，在独立标高横移，再竖直接入主管；不限制高差方向。
                lead.Z = start.Z;
                Point3D raisedLead = new Point3D(lead.X, lead.Y, start.Z + bendOffset);
                Point3D raisedJoin = new Point3D(join.X, join.Y, raisedLead.Z);
                List<Point3D> detour = new List<Point3D> { start, lead, raisedLead, raisedJoin };
                if ((join - raisedJoin).Length > 1e-9) detour.Add(join);
                for (int i = 1; i < detour.Count; i++)
                    if ((detour[i] - detour[i - 1]).Length <= minLength)
                        throw new InvalidOperationException("冷凝水错层路线包含过短管段。");
                return detour.ToArray();
            }
            double drop = start.Z - join.Z;
            // 不输入或指定坡度；把实际高差分配到平面路径上，同标高时保持水平。
            lead.Z = start.Z - drop * leadLength / (leadLength + crossLength) + bendOffset;

            List<Point3D> points = new List<Point3D> { start };
            // 同向直线合并，避免在共线管段之间强行创建弯头。
            if (alignment < 1 - 1e-6 || Math.Abs(bendOffset) > 1e-9) points.Add(lead);
            points.Add(join);
            for (int i = 1; i < points.Count; i++)
                if ((points[i] - points[i - 1]).Length <= minLength)
                    throw new InvalidOperationException("冷凝水路线包含过短管段。");
            return points.ToArray();
        }

        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private static bool Valid(Point3D p) { return Finite(p.X) && Finite(p.Y) && Finite(p.Z); }
    }
}
