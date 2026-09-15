using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using static FCUAutoDesign.RevitUnits;

namespace FCUAutoDesign
{
    internal sealed class RoomRuleSnapshot
    {
        public bool IsValid { get; internal set; }
        public string ErrorMessage { get; internal set; }
        public double LengthM { get; internal set; }
        public double WidthM { get; internal set; }
        public double BaseElevationM { get; internal set; }
    }

    internal sealed class RoomRuleSnapshotReader
    {
        private const double ToleranceFeet = 1.0 / 304.8;

        public RoomRuleSnapshot Read(Room room)
        {
            RoomRuleSnapshot result = new RoomRuleSnapshot();
            if (room == null || room.Level == null)
                return Fail(result, "房间或房间标高无效。");

            IList<IList<BoundarySegment>> loops = room.GetBoundarySegments(
                new SpatialElementBoundaryOptions());
            if (loops == null || loops.Count == 0)
                return Fail(result, "房间没有可读取的边界。");

            IList<BoundarySegment> outer = loops
                .OrderByDescending(x => x.Sum(s => s.GetCurve().Length)).FirstOrDefault();
            if (outer == null || outer.Count < 4)
                return Fail(result, "房间边界不足以识别矩形局部基线。");

            List<Curve> curves = outer.Select(x => x.GetCurve()).ToList();
            if (curves.Any(x => !(x is Line)))
                return Fail(result, "房间包含弧形边界，当前规则预览要求人工确认 L/H。");

            Line first = curves.Cast<Line>().FirstOrDefault(x => x.Length > ToleranceFeet);
            if (first == null)
                return Fail(result, "房间边界没有有效直线。");
            XYZ axis = new XYZ(first.Direction.X, first.Direction.Y, 0).Normalize();
            XYZ perpendicular = XYZ.BasisZ.CrossProduct(axis).Normalize();
            List<XYZ> points = curves.SelectMany(x => new[] { x.GetEndPoint(0), x.GetEndPoint(1) }).ToList();
            double minAxis = points.Min(x => x.DotProduct(axis));
            double maxAxis = points.Max(x => x.DotProduct(axis));
            double minPerpendicular = points.Min(x => x.DotProduct(perpendicular));
            double maxPerpendicular = points.Max(x => x.DotProduct(perpendicular));
            double axisLength = maxAxis - minAxis;
            double perpendicularLength = maxPerpendicular - minPerpendicular;
            if (axisLength <= ToleranceFeet || perpendicularLength <= ToleranceFeet)
                return Fail(result, "房间局部边界尺寸无效。");

            foreach (XYZ point in points)
            {
                double a = point.DotProduct(axis);
                double b = point.DotProduct(perpendicular);
                bool onAxisEdge = Near(a, minAxis) || Near(a, maxAxis);
                bool onPerpendicularEdge = Near(b, minPerpendicular) || Near(b, maxPerpendicular);
                if (!onAxisEdge && !onPerpendicularEdge)
                    return Fail(result, "房间边界不是可识别的矩形，未使用世界坐标包围盒替代。");
            }

            result.IsValid = true;
            result.LengthM = Math.Max(axisLength, perpendicularLength) * FEET_TO_MM / 1000.0;
            result.WidthM = Math.Min(axisLength, perpendicularLength) * FEET_TO_MM / 1000.0;
            result.BaseElevationM = room.Level.Elevation * FEET_TO_MM / 1000.0;
            return result;
        }

        private static bool Near(double left, double right)
        {
            return Math.Abs(left - right) <= ToleranceFeet;
        }

        private static RoomRuleSnapshot Fail(RoomRuleSnapshot result, string message)
        {
            result.IsValid = false;
            result.ErrorMessage = message;
            return result;
        }
    }
}
