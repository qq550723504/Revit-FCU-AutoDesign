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

            FamilyInstance door = selectedDoor ?? ResolveDoorForRoom(doc, room);
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

        // Returns a deterministic door when all associated doors lie on one
        // horizontal wall edge. Returns null when the room has no door or the
        // doors are on different edges and a user pick is required.
        internal static FamilyInstance ResolveDoorForRoom(Document doc, Room room)
        {
            List<FamilyInstance> doors = FindDoors(doc, room);
            if (doors.Count <= 1)
                return doors.SingleOrDefault();

            List<Line> wallLines = doors.Select(x => (x.Host as Wall)?.Location as LocationCurve)
                .Select(x => x?.Curve as Line).ToList();
            if (wallLines.Any(x => x == null) || !AreCollinearWallLines(wallLines))
                return null;

            // Same-edge doors have the same L/H axes. For the placement point,
            // choose the door nearest the room centre, then ElementId as a
            // stable tie-breaker; no geometric guess is made across walls.
            BoundingBoxXYZ bounds = room.get_BoundingBox(null);
            XYZ center = bounds == null ? null : (bounds.Min + bounds.Max) * 0.5;
            return doors.OrderBy(x => DoorDistanceSquared(x, center))
                .ThenBy(x => x.Id.IntegerValue).First();
        }

        private static bool AreCollinearWallLines(IList<Line> lines)
        {
            const double tolerance = 1.0 / 304.8;
            Line first = lines[0];
            if (Math.Abs(first.Direction.Z) > tolerance) return false;
            XYZ firstDirection = new XYZ(first.Direction.X, first.Direction.Y, 0);
            if (firstDirection.GetLength() <= tolerance) return false;
            firstDirection = firstDirection.Normalize();
            XYZ perpendicular = XYZ.BasisZ.CrossProduct(firstDirection).Normalize();
            XYZ firstPoint = first.GetEndPoint(0);
            foreach (Line line in lines.Skip(1))
            {
                if (Math.Abs(line.Direction.Z) > tolerance) return false;
                XYZ direction = new XYZ(line.Direction.X, line.Direction.Y, 0);
                if (direction.GetLength() <= tolerance
                    || Math.Abs(firstDirection.DotProduct(direction.Normalize())) < 1.0 - 1e-6)
                    return false;
                if (Math.Abs((line.GetEndPoint(0) - firstPoint).DotProduct(perpendicular)) > tolerance)
                    return false;
            }
            return true;
        }

        private static double DoorDistanceSquared(FamilyInstance door, XYZ center)
        {
            LocationPoint location = door.Location as LocationPoint;
            if (center == null || location == null) return double.MaxValue;
            XYZ delta = location.Point - center;
            return delta.DotProduct(delta);
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

        internal static XYZ GetRoomCenter(Document doc, Room room, XYZ axis)
        {
            if (doc == null || room == null || axis == null) return null;
            IList<IList<BoundarySegment>> loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions());
            IList<BoundarySegment> outer = loops?.OrderByDescending(x => x.Sum(s => s.GetCurve().Length)).FirstOrDefault();
            if (outer == null || outer.Count == 0) return null;
            XYZ localAxis = new XYZ(axis.X, axis.Y, 0);
            if (localAxis.GetLength() <= ToleranceFeet) return null;
            localAxis = localAxis.Normalize();
            XYZ perpendicular = XYZ.BasisZ.CrossProduct(localAxis).Normalize();
            List<XYZ> points = outer.SelectMany(x => new[] { x.GetCurve().GetEndPoint(0), x.GetCurve().GetEndPoint(1) }).ToList();
            if (points.Count == 0 || points.Any(x => Math.Abs(x.Z) > 1e6)) return null;
            double minA = points.Min(x => x.DotProduct(localAxis));
            double maxA = points.Max(x => x.DotProduct(localAxis));
            double minB = points.Min(x => x.DotProduct(perpendicular));
            double maxB = points.Max(x => x.DotProduct(perpendicular));
            XYZ center = localAxis * ((minA + maxA) * 0.5) + perpendicular * ((minB + maxB) * 0.5);
            XYZ test = new XYZ(center.X, center.Y, room.Level.Elevation + 1.0);
            return room.IsPointInRoom(test) ? center : null;
        }

        internal static XYZ GetDoorWallOffsetCenter(Document doc, Room room, Wall wall, double offsetMm)
        {
            if (doc == null || room == null || wall == null || offsetMm <= 0
                || double.IsNaN(offsetMm) || double.IsInfinity(offsetMm)) return null;
            Line wallLine = (wall.Location as LocationCurve)?.Curve as Line;
            if (wallLine == null) return null;
            XYZ axis = new XYZ(wallLine.Direction.X, wallLine.Direction.Y, 0).Normalize();
            XYZ perpendicular = XYZ.BasisZ.CrossProduct(axis).Normalize();
            IList<IList<BoundarySegment>> loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions());
            IList<BoundarySegment> outer = loops?.OrderByDescending(x => x.Sum(s => s.GetCurve().Length)).FirstOrDefault();
            if (outer == null || outer.Count == 0) return null;
            List<XYZ> points = outer.SelectMany(x => new[] { x.GetCurve().GetEndPoint(0), x.GetCurve().GetEndPoint(1) }).ToList();
            if (points.Count == 0) return null;
            double minA = points.Min(x => x.DotProduct(axis));
            double maxA = points.Max(x => x.DotProduct(axis));
            double along = (minA + maxA) * 0.5;
            XYZ wallOrigin = wallLine.GetEndPoint(0);
            XYZ wallMid = wallOrigin + axis * (along - wallOrigin.DotProduct(axis));
            XYZ roomCenter = GetRoomCenter(doc, room, axis);
            if (roomCenter == null) return null;
            XYZ inward = roomCenter - wallMid;
            inward = new XYZ(inward.X, inward.Y, 0);
            if (inward.GetLength() <= ToleranceFeet) return null;
            inward = inward.Normalize();
            double wallHalfWidth = Math.Max(0, wall.Width * 0.5);
            XYZ candidate = wallMid + inward * (wallHalfWidth + offsetMm * MM_TO_FEET);
            XYZ test = new XYZ(candidate.X, candidate.Y, room.Level.Elevation + 1.0);
            return room.IsPointInRoom(test) ? candidate : null;
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
