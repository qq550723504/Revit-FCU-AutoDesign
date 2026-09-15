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

        public RoomRuleSnapshot Read(Document doc, Room room, FamilyInstance selectedDoor = null)
        {
            RoomRuleSnapshot result = new RoomRuleSnapshot();
            if (room == null || room.Level == null)
                return Fail(result, "房间或房间标高无效。");

            FamilyInstance door = selectedDoor ?? FindDoorForRoom(doc, room);
            if (selectedDoor != null && !BelongsToRoom(selectedDoor, room))
                return Fail(result, "所选门不属于当前房间。");
            Wall doorWall = door == null ? null : door.Host as Wall;
            Line doorWallLine = doorWall == null
                ? null
                : (doorWall.Location as LocationCurve)?.Curve as Line;
            if (doorWallLine == null)
                return Fail(result, "需要房间唯一关联门及其所在的水平直墙；门所在墙方向定义 L 轴。");
            XYZ doorAxis = new XYZ(doorWallLine.Direction.X, doorWallLine.Direction.Y, 0);
            if (doorAxis.GetLength() <= ToleranceFeet)
                return Fail(result, "无法从门所在墙确定 L 轴。");
            doorAxis = doorAxis.Normalize();

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

            XYZ axis = doorAxis;
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
            // L is defined by the wall hosting the room's door, regardless of which
            // room dimension is longer. H is the perpendicular room dimension.
            result.LengthM = axisLength * FEET_TO_MM / 1000.0;
            result.WidthM = perpendicularLength * FEET_TO_MM / 1000.0;
            result.BaseElevationM = room.Level.Elevation * FEET_TO_MM / 1000.0;
            return result;
        }

        private static FamilyInstance FindDoorForRoom(Document doc, Room room)
        {
            List<FamilyInstance> doors = FindDoors(doc, room);
            if (doors.Count != 1)
                return null;
            return doors[0];
        }

        internal static List<FamilyInstance> FindDoors(Document doc, Room room)
        {
            if (doc == null || room == null) return new List<FamilyInstance>();
            return new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(door =>
                    (door.Room != null && door.Room.Id == room.Id)
                    || (door.ToRoom != null && door.ToRoom.Id == room.Id)
                    || (door.FromRoom != null && door.FromRoom.Id == room.Id))
                .ToList();
        }

        internal static bool BelongsToRoom(FamilyInstance door, Room room)
        {
            return door != null && room != null
                && ((door.Room != null && door.Room.Id == room.Id)
                    || (door.ToRoom != null && door.ToRoom.Id == room.Id)
                    || (door.FromRoom != null && door.FromRoom.Id == room.Id));
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
