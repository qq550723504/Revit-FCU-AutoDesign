using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using static FCUAutoDesign.RevitUnits;

namespace FCUAutoDesign
{
    internal class FcuPlacementService
    {
        private readonly FcuConnectorResolver connectors = new FcuConnectorResolver();

        // 调用方必须已开启主事务；此服务不提交主事务。
        public IList<XYZ> PlanPoints(Document doc, Room room, FcuDesignOptions options)
        {
            FamilyInstance door;
            RoomRuleSnapshot snapshot = ReadRuleSnapshot(doc, room, options, out door);
            Wall wall = door.Host as Wall;
            XYZ center = RoomRuleSnapshotReader.GetDoorWallOffsetCenter(doc, room, wall, options.DoorOffsetMm);
            if (center == null) throw new InvalidOperationException("无法确定距门侧墙的合法布置基线。未放置设备。");
            XYZ axis = ((wall.Location as LocationCurve).Curve as Line).Direction;
            var planner = new Business.EquipmentSelection.EquipmentPlacementPlanner();
            var local = planner.Build(snapshot.LengthM, snapshot.WidthM, snapshot.BaseElevationM,
                options.FcuElevationMm / 1000.0, options.DoorOffsetMm / 1000.0,
                Business.EquipmentSelection.EquipmentPlacementPlanner.GetUnitCount(snapshot.LengthM));
            var points = local.Select(p =>
            {
                XYZ xy = center + axis * ((p.XAlongLengthM - snapshot.LengthM / 2) * 1000 * MM_TO_FEET);
                return new XYZ(xy.X, xy.Y, p.AbsoluteElevationM * 1000 * MM_TO_FEET);
            }).ToList();
            if (points.Any(p => !room.IsPointInRoom(p)))
                throw new InvalidOperationException("至少一个多台 FCU 落点不在房间体积内；整间未放置，请检查边界、内缩距离和标高。");
            return points;
        }

        public RoomRuleSnapshot ReadRuleSnapshot(Document doc, Room room, FcuDesignOptions options)
        {
            FamilyInstance ignored;
            return ReadRuleSnapshot(doc, room, options, out ignored);
        }

        private RoomRuleSnapshot ReadRuleSnapshot(Document doc, Room room, FcuDesignOptions options,
            out FamilyInstance door)
        {
            door = FindDoorForRoom(doc, room, options);
            RoomRuleSnapshot snapshot = new RoomRuleSnapshotReader().Read(doc, room, door);
            if (!snapshot.IsValid) throw new InvalidOperationException(snapshot.ErrorMessage);
            return snapshot;
        }

        public FcuPlacementResult Place(Document doc, Room room, FcuDesignOptions options, XYZ plannedPoint = null)
        {
            if (room.Level == null || room.Area <= 0 || room.get_BoundingBox(null) == null)
                throw new InvalidOperationException("目标房间必须具有有效标高、面积和边界。");
            double baseLevelElev = room.Level.Elevation;
            double fcuAbsoluteZ = baseLevelElev + (options.FcuElevationMm * MM_TO_FEET);

            FamilyInstance door = FindDoorForRoom(doc, room, options);
            Wall doorWall = door?.Host as Wall;
            Line doorWallLine = (doorWall?.Location as LocationCurve)?.Curve as Line;
            if (doorWallLine == null)
                throw new InvalidOperationException("需要房间关联门及其所在的直墙，才能保证 FCU 出风口平面与门侧墙面平行。请检查门的关联房间和宿主墙。");
            XYZ wallDirection = doorWallLine.Direction;
            if (Math.Abs(wallDirection.Z) > 1e-6)
                throw new InvalidOperationException("门所在墙的定位线不是水平直线，当前验证版无法确定出风方向。");
            XYZ placePoint;
            XYZ forwardDir;

            // 已验证门与宿主墙，落点方向与出风方向分别计算。
            {
                LocationPoint doorLoc = door.Location as LocationPoint;
                if (doorLoc == null)
                    throw new InvalidOperationException("房间关联门没有有效定位点。");
                XYZ doorPoint = doorLoc.Point;
                forwardDir = door.FacingOrientation;

                // 几何落点验证：门法向可能背对房间，若偏向走廊必须反转矢量
                XYZ candidatePt = doorPoint + forwardDir * (options.DoorOffsetMm * MM_TO_FEET);
                XYZ testCheckPt = new XYZ(candidatePt.X, candidatePt.Y, baseLevelElev + 1.0);

                if (!room.IsPointInRoom(testCheckPt))
                {
                    // 尝试反向
                    forwardDir = -forwardDir;
                    candidatePt = doorPoint + forwardDir * (options.DoorOffsetMm * MM_TO_FEET);
                    testCheckPt = new XYZ(candidatePt.X, candidatePt.Y, baseLevelElev + 1.0);

                    if (!room.IsPointInRoom(testCheckPt))
                    {
                        // 容错回退：朝向房间中心
                        BoundingBoxXYZ bbox = room.get_BoundingBox(null);
                        XYZ center = (bbox.Min + bbox.Max) * 0.5;
                        XYZ toCenter = new XYZ(center.X - doorPoint.X, center.Y - doorPoint.Y, 0).Normalize();
                        candidatePt = doorPoint + toCenter * (options.DoorOffsetMm * MM_TO_FEET);
                        forwardDir = toCenter;
                    }
                }

                XYZ targetPoint = plannedPoint ?? RoomRuleSnapshotReader.GetDoorWallOffsetCenter(
                    doc, room, doorWall, options.DoorOffsetMm);
                if (targetPoint != null)
                {
                    candidatePt = targetPoint;
                    // 位置规则不改变门侧朝向，只改变设备落点。
                    testCheckPt = new XYZ(candidatePt.X, candidatePt.Y, baseLevelElev + 1.0);
                }
                placePoint = new XYZ(candidatePt.X, candidatePt.Y, fcuAbsoluteZ);
            }

            // 按用户实测要求反转上一版姿态 180°：接口法向垂直于墙，取内缩方向的反向。
            XYZ wallNormal = XYZ.BasisZ.CrossProduct(wallDirection).Normalize();
            double inwardProjection = wallNormal.DotProduct(forwardDir);
            if (Math.Abs(inwardProjection) < 1e-6)
                throw new InvalidOperationException("无法确定门侧墙朝向房间的法向，请检查门与房间位置。");
            XYZ expectedOutletDirection = inwardProjection < 0 ? wallNormal : -wallNormal;

            // 所有候选路径都必须通过最终实际安装高度的房间包含检查。
            if (!room.IsPointInRoom(placePoint))
                throw new InvalidOperationException("FCU 候选点不在目标房间体积内，请调整内缩距离、安装高度或检查房间上限。未放置设备。");

            // 放置风盘族实例
            FamilySymbol fcuSymbol = doc.GetElement(new ElementId(options.SelectedFcuTypeId)) as FamilySymbol;
            if (fcuSymbol == null)
                throw new InvalidOperationException("当前项目中未找到风机盘管族(FCU)，请先在项目中载入机械设备族！");

            if (!fcuSymbol.IsActive) fcuSymbol.Activate();
            FamilyInstance fcu = doc.Create.NewFamilyInstance(placePoint, fcuSymbol, StructuralType.NonStructural);

            // 用真实送风接口确定出风方向，不假定族的局部 X 轴就是出风轴。
            doc.Regenerate();
            AlignSupplyOutlet(doc, fcu, placePoint, expectedOutletDirection);

            // 核心步骤：必须 Regenerate 刷新拓扑以获得正确的绝对连接件坐标
            doc.Regenerate();

            LocationPoint actualLocation = fcu.Location as LocationPoint;
            if (actualLocation == null || !room.IsPointInRoom(actualLocation.Point)
                || actualLocation.Point.DistanceTo(placePoint) > MM_TO_FEET)
                throw new InvalidOperationException("FCU 实际定位点不在房间内或偏离预期安装位置，已取消本次操作。");
            return new FcuPlacementResult { Instance = fcu, Point = placePoint, ExpectedOutletDirection = expectedOutletDirection };
        }

        private FamilyInstance FindDoorForRoom(Document doc, Room room, FcuDesignOptions options)
        {
            ElementId selectedDoorId;
            if (options != null && options.SelectedDoorIds != null
                && options.SelectedDoorIds.TryGetValue(room.Id.IntegerValue, out selectedDoorId))
            {
                FamilyInstance selectedDoor = doc.GetElement(selectedDoorId) as FamilyInstance;
                if (!RoomRuleSnapshotReader.BelongsToRoom(selectedDoor, room))
                    throw new InvalidOperationException("所选目标门不属于当前房间，无法确定 L 轴。");
                return selectedDoor;
            }

            FamilyInstance automaticDoor = RoomRuleSnapshotReader.ResolveDoorForRoom(doc, room);
            if (automaticDoor != null)
                return automaticDoor;

            FilteredElementCollector collector = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .OfClass(typeof(FamilyInstance));

            List<FamilyInstance> doors = collector.Cast<FamilyInstance>().Where(door =>
                (door.Room != null && door.Room.Id == room.Id)
                || (door.ToRoom != null && door.ToRoom.Id == room.Id)
                || (door.FromRoom != null && door.FromRoom.Id == room.Id)).ToList();
            if (doors.Count > 1)
                throw new InvalidOperationException("房间关联了多个门，请先拾取作为 L 侧的目标门。");
            return doors.SingleOrDefault();
        }

        private void AlignSupplyOutlet(Document doc, FamilyInstance fcu, XYZ origin, XYZ target)
        {
            XYZ outletDirection = connectors.GetSupplyAirOutlet(fcu).CoordinateSystem.BasisZ.Normalize();
            if (Math.Abs(outletDirection.Z) > 1e-6)
                throw new InvalidOperationException("FCU 送风接口不是水平出风，无法通过平面旋转使出风口平面与门侧墙平行。");
            double angle = Math.Atan2(outletDirection.CrossProduct(target).Z,
                outletDirection.DotProduct(target));
            ElementTransformUtils.RotateElement(doc, fcu.Id, Line.CreateUnbound(origin, XYZ.BasisZ), angle);
            doc.Regenerate();
            VerifySupplyOutletDirection(fcu, target);
        }

        public void VerifySupplyOutletDirection(FamilyInstance fcu, XYZ target)
        {
            XYZ outletDirection = connectors.GetSupplyAirOutlet(fcu).CoordinateSystem.BasisZ.Normalize();
            if (target == null || outletDirection.DistanceTo(target) > 1e-6)
                throw new InvalidOperationException("FCU 出风口平面未与门侧墙平行，或接口方向不符合已确认的反转朝向，已取消本次操作。");
        }
    }
}
