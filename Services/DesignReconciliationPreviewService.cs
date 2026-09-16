using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using FCUAutoDesign.Business.DesignReconciliation;
using static FCUAutoDesign.RevitUnits;

namespace FCUAutoDesign
{
    internal enum ReconciliationPreviewOutcome
    {
        NotHandled,
        PreviewOnly,
        Applied
    }

    internal sealed class DesignReconciliationPreviewService
    {
        private readonly FcuPlacementService placement = new FcuPlacementService();
        private readonly FcuConnectorResolver connectors = new FcuConnectorResolver();
        private readonly DesignReconciliationPlanner planner = new DesignReconciliationPlanner();
        private readonly ReconciliationApplyPolicy applyPolicy = new ReconciliationApplyPolicy();

        public ReconciliationPreviewOutcome ShowIfExisting(Document doc, IList<Room> rooms, FcuDesignOptions options)
        {
            DesignRecordRepository repository = new DesignRecordRepository(doc);
            List<Tuple<Room, DesignRecord>> existing = new List<Tuple<Room, DesignRecord>>();
            List<Room> newRooms = new List<Room>();
            foreach (Room room in rooms)
            {
                DesignRecordReadResult read = repository.LoadByRoomUniqueId(room.UniqueId);
                if (read.Status == DesignOperationStatus.Succeeded)
                    existing.Add(Tuple.Create(room, read.Record));
                else if (read.Status == DesignOperationStatus.NotFound)
                    newRooms.Add(room);
                else
                    throw new InvalidOperationException("读取房间设计记录失败：" + read.Message);
            }
            if (existing.Count == 0) return ReconciliationPreviewOutcome.NotHandled;

            StringBuilder summary = new StringBuilder();
            StringBuilder details = new StringBuilder();
            List<ReconciliationSession> sessions = new List<ReconciliationSession>();
            int conflicts = 0, creates = 0, updates = 0, deletes = 0, manual = 0;
            foreach (Tuple<Room, DesignRecord> entry in existing)
            {
                Room room = entry.Item1;
                DesignRecord record = entry.Item2;
                DesignSnapshot baseline = record.LastSuccessfulSnapshot;
                if (baseline == null) throw new InvalidOperationException("设计记录缺少上次成功快照。");
                DesignSnapshot current = BuildCurrent(doc, room, options, baseline);
                DesignSnapshot desired = BuildDesired(doc, room, options, record, baseline);
                ReconciliationPlan plan = planner.BuildPlan(new ReconciliationRequest
                {
                    Baseline = baseline,
                    Current = current,
                    Desired = desired,
                    ExpectedCurrentRevision = record.Version?.Revision,
                    ExpectedCurrentFingerprint = current.Fingerprint
                });
                if (plan.ErrorCode != DesignErrorCode.None)
                    throw new InvalidOperationException("重算预览失败：" + plan.ErrorMessage);
                ReconciliationApplyDecision decision = applyPolicy.Evaluate(plan);
                sessions.Add(new ReconciliationSession
                {
                    Room = room,
                    Record = record,
                    Current = current,
                    Desired = desired,
                    Plan = plan,
                    ApplyDecision = decision
                });
                string label = (room.Number ?? room.Id.IntegerValue.ToString()) + " " + (room.Name ?? string.Empty);
                summary.AppendLine(label + "：" + (plan.Items.Count == 0 ? "无变化" : plan.Items.Count + " 项差异"));
                details.AppendLine(label + "（DesignId " + record.Identity.DesignId + "，Revision "
                    + record.Version.Revision + " → " + (record.Version.Revision + 1) + "）");
                if (plan.Items.Count == 0) details.AppendLine("  无变化：重复执行不会新增元素。");
                foreach (ReconciliationItem item in plan.Items)
                {
                    details.AppendLine("  [" + ActionText(item.Action) + "] " + KindText(item.EntityKind)
                        + " " + item.LogicalId + "；字段："
                        + (item.ChangedFields.Count == 0 ? "无" : string.Join("、", item.ChangedFields))
                        + "；" + item.Message);
                    switch (item.Action)
                    {
                        case ReconciliationAction.Create: creates++; break;
                        case ReconciliationAction.Update: updates++; break;
                        case ReconciliationAction.DeleteCandidate: deletes++; break;
                        case ReconciliationAction.Conflict: conflicts++; break;
                        case ReconciliationAction.NoChange: manual++; break;
                    }
                }
                details.AppendLine("  当前指纹：" + current.Fingerprint);
                details.AppendLine("  写入判定：" + decision.Reason);
                details.AppendLine();
            }
            foreach (Room room in newRooms)
                details.AppendLine((room.Number ?? room.Id.IntegerValue.ToString()) + " " + room.Name
                    + "：没有设计记录；本次混合选择不执行新建。");

            bool canApply = newRooms.Count == 0 && sessions.Count > 0
                && sessions.All(x => x.ApplyDecision.CanApplyRecordOnly);
            TaskDialog dialog = new TaskDialog(canApply ? "FCU-205 计算记录更新" : "FCU-205 只读重算预览")
            {
                MainInstruction = canApply
                    ? "仅检测到冷指标或设计负荷变化，是否更新设计记录？"
                    : "检测到已有 FCU 设计，本次仅显示差异",
                MainContent = "新增 " + creates + "，更新 " + updates + "，删除候选 " + deletes
                    + "，人工修改保留 " + manual + "，冲突 " + conflicts + "。"
                    + Environment.NewLine + summary,
                ExpandedContent = details.ToString(),
                FooterText = canApply
                    ? "确认后仅更新冷指标、设计负荷和 Revision；不移动或替换设备，不修改管线和管径。"
                    : "本次未选择主管、未修改模型、未更新 Revision；正式自动管径仍未启用。",
                CommonButtons = canApply
                    ? TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No
                    : TaskDialogCommonButtons.Close,
                DefaultButton = canApply ? TaskDialogResult.No : TaskDialogResult.Close
            };
            TaskDialogResult answer = dialog.Show();
            if (!canApply || answer != TaskDialogResult.Yes)
                return ReconciliationPreviewOutcome.PreviewOnly;

            ApplyCalculationRecords(doc, repository, sessions, options);
            TaskDialog.Show("FCU-205 更新完成", "已更新 " + sessions.Count
                + " 个房间的计算记录。未移动或替换设备，未修改任何管线和管径。");
            return ReconciliationPreviewOutcome.Applied;
        }

        private void ApplyCalculationRecords(Document doc, DesignRecordRepository repository,
            IList<ReconciliationSession> sessions, FcuDesignOptions options)
        {
            // The dialog creates a time gap. Re-capture model state before writing so an
            // element changed while the preview was open cannot be accepted as this baseline.
            foreach (ReconciliationSession session in sessions)
            {
                DesignSnapshot fresh = BuildCurrent(doc, session.Room, options,
                    session.Record.LastSuccessfulSnapshot);
                if (!string.Equals(fresh.Fingerprint, session.Current.Fingerprint, StringComparison.Ordinal))
                    throw new InvalidOperationException("房间 " + session.Room.Number
                        + " 的模型在预览后已变化，请重新执行重算。");
            }

            using (TransactionGroup group = new TransactionGroup(doc, "FCU-205 更新计算设计记录"))
            {
                group.Start();
                try
                {
                    using (Transaction transaction = new Transaction(doc, "更新 FCU 冷指标与设计负荷记录"))
                    {
                        transaction.Start();
                        foreach (ReconciliationSession session in sessions)
                        {
                            DesignVersion expected = CopyVersion(session.Record.Version);
                            DesignSnapshot baseline = session.Desired;
                            baseline.Kind = DesignSnapshotKind.Baseline;
                            baseline.Status = DesignSnapshotStatus.Valid;
                            baseline.CapturedAtUtc = DateTime.UtcNow;
                            baseline.Fingerprint = DesignSnapshotFingerprint.Compute(baseline);
                            DesignRecord updated = new DesignRecord
                            {
                                Identity = new DesignIdentity
                                {
                                    DesignId = session.Record.Identity.DesignId,
                                    HostDocumentId = session.Record.Identity.HostDocumentId
                                },
                                Version = CopyVersion(baseline.Version),
                                Status = DesignRecordStatus.Committed,
                                LastSuccessfulSnapshot = baseline,
                                CurrentSnapshot = null,
                                DesiredSnapshot = null
                            };
                            DesignOperationResult saved = repository.Save(new DesignWriteRequest
                            {
                                Record = updated,
                                ExpectedVersion = expected
                            });
                            if (saved.Status != DesignOperationStatus.Succeeded)
                                throw new InvalidOperationException("保存房间 " + session.Room.Number
                                    + " 的设计记录失败：" + saved.Message);
                            session.AppliedFingerprint = baseline.Fingerprint;
                            session.AppliedRevision = baseline.Version.Revision;
                        }
                        if (transaction.Commit() != TransactionStatus.Committed)
                            throw new InvalidOperationException("FCU 计算设计记录事务未能提交。");
                    }

                    foreach (ReconciliationSession session in sessions)
                    {
                        DesignRecordReadResult verified = repository.LoadByRoomUniqueId(session.Room.UniqueId);
                        if (verified.Status != DesignOperationStatus.Succeeded
                            || verified.Record == null
                            || verified.Record.Version == null
                            || verified.Record.LastSuccessfulSnapshot == null
                            || verified.Record.Version.Revision != session.AppliedRevision
                            || !string.Equals(verified.Record.LastSuccessfulSnapshot.Fingerprint,
                                session.AppliedFingerprint, StringComparison.Ordinal))
                            throw new InvalidOperationException("房间 " + session.Room.Number
                                + " 的设计记录提交后校验失败。");
                    }
                    if (group.Assimilate() != TransactionStatus.Committed)
                        throw new InvalidOperationException("FCU 计算设计记录事务组未能合并。");
                }
                catch
                {
                    if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
                    throw;
                }
            }
        }

        private sealed class ReconciliationSession
        {
            public Room Room { get; set; }
            public DesignRecord Record { get; set; }
            public DesignSnapshot Current { get; set; }
            public DesignSnapshot Desired { get; set; }
            public ReconciliationPlan Plan { get; set; }
            public ReconciliationApplyDecision ApplyDecision { get; set; }
            public int AppliedRevision { get; set; }
            public string AppliedFingerprint { get; set; }
        }

        private DesignSnapshot BuildCurrent(Document doc, Room room, FcuDesignOptions options, DesignSnapshot baseline)
        {
            DesignSnapshot current = NewSnapshot(DesignSnapshotKind.Current, baseline.Version);
            DesignRoomRecord baselineRoom = baseline.Rooms.Single(x => x.RoomUniqueId == room.UniqueId);
            RoomRuleSnapshot dimensions = ReadDimensions(doc, room, options);
            DesignRoomRecord currentRoom = CopyRoom(baselineRoom);
            currentRoom.RoomNumber = room.Number ?? string.Empty;
            currentRoom.RoomName = room.Name ?? string.Empty;
            currentRoom.LevelUniqueId = room.Level?.UniqueId ?? string.Empty;
            if (dimensions.IsValid)
            {
                currentRoom.Length = new DesignQuantity(dimensions.LengthM, DesignUnit.Meter);
                currentRoom.Height = new DesignQuantity(dimensions.WidthM, DesignUnit.Meter);
            }
            foreach (DesignDeviceRecord source in baseline.Devices)
            {
                FamilyInstance instance = doc.GetElement(source.ElementUniqueId) as FamilyInstance;
                LocationPoint location = instance?.Location as LocationPoint;
                if (instance == null || location == null) continue;
                DesignDeviceRecord device = CopyDevice(source);
                device.FamilySymbolUniqueId = instance.Symbol.UniqueId;
                device.CatalogItemId = "unconfirmed:" + instance.Symbol.Name;
                device.Position = Point(location.Point);
                device.Orientation = Direction(connectors.GetSupplyAirOutlet(instance).CoordinateSystem.BasisZ.Normalize());
                current.Devices.Add(device);
                currentRoom.DeviceLogicalIds.Add(device.LogicalDeviceId);
            }
            foreach (DesignPipeRecord source in baseline.Pipes)
            {
                Element element = doc.GetElement(source.ElementUniqueId);
                if (element == null) continue;
                DesignPipeRecord pipe = CopyPipe(source);
                pipe.CurrentDiameter = Diameter(element) ?? source.CurrentDiameter;
                current.Pipes.Add(pipe);
                currentRoom.PipeLogicalIds.Add(pipe.LogicalPipeId);
            }
            currentRoom.UnitCount = current.Devices.Count;
            current.Rooms.Add(currentRoom);
            current.Fingerprint = DesignSnapshotFingerprint.Compute(current);
            return current;
        }

        private DesignSnapshot BuildDesired(Document doc, Room room, FcuDesignOptions options,
            DesignRecord record, DesignSnapshot baseline)
        {
            DesignVersion desiredVersion = CopyVersion(record.Version);
            desiredVersion.Revision++;
            DesignSnapshot desired = NewSnapshot(DesignSnapshotKind.Desired, desiredVersion);
            DesignRoomRecord baselineRoom = baseline.Rooms.Single(x => x.RoomUniqueId == room.UniqueId);
            RoomRuleSnapshot dimensions = ReadDimensions(doc, room, options);
            IList<XYZ> points;
            if (options.EnableMultipleFcus)
            {
                points = placement.PlanPoints(doc, room, options);
            }
            else
            {
                XYZ center = RoomRuleSnapshotReader.GetDoorWallOffsetCenter(doc, room,
                    ResolveDoor(doc, room, options).Host as Wall, options.DoorOffsetMm);
                points = center == null ? new XYZ[] { null } : new[]
                {
                    new XYZ(center.X, center.Y, room.Level.Elevation + options.FcuElevationMm * MM_TO_FEET)
                };
            }
            if (points.Any(x => x == null)) throw new InvalidOperationException("无法计算期望 FCU 点位。");
            FamilySymbol symbol = doc.GetElement(new ElementId(options.SelectedFcuTypeId)) as FamilySymbol;
            if (symbol == null) throw new InvalidOperationException("所选 FCU 族类型已不存在。");
            List<DesignDeviceRecord> ordered = baseline.Devices.OrderBy(x => x.LogicalDeviceId, StringComparer.Ordinal).ToList();
            double load = dimensions.LengthM * dimensions.WidthM * options.CoolingIndexWPerSquareMeter / 1000.0;
            DesignRoomRecord desiredRoom = CopyRoom(baselineRoom);
            desiredRoom.RoomNumber = room.Number ?? string.Empty;
            desiredRoom.RoomName = room.Name ?? string.Empty;
            desiredRoom.LevelUniqueId = room.Level?.UniqueId ?? string.Empty;
            desiredRoom.Length = new DesignQuantity(dimensions.LengthM, DesignUnit.Meter);
            desiredRoom.Height = new DesignQuantity(dimensions.WidthM, DesignUnit.Meter);
            desiredRoom.CoolingLoadDensity = new DesignQuantity(options.CoolingIndexWPerSquareMeter, DesignUnit.WattPerSquareMeter);
            desiredRoom.DesignCoolingLoad = new DesignQuantity(load, DesignUnit.Kilowatt);
            desiredRoom.UnitCount = points.Count;
            for (int i = 0; i < points.Count; i++)
            {
                DesignDeviceRecord previous = i < ordered.Count ? ordered[i] : null;
                string logicalId = previous?.LogicalDeviceId ?? record.Identity.DesignId + ":device:" + (i + 1);
                desiredRoom.DeviceLogicalIds.Add(logicalId);
                desired.Devices.Add(new DesignDeviceRecord
                {
                    LogicalDeviceId = logicalId,
                    ElementUniqueId = previous?.ElementUniqueId,
                    RoomUniqueId = room.UniqueId,
                    CatalogItemId = "unconfirmed:" + symbol.Name,
                    FamilySymbolUniqueId = symbol.UniqueId,
                    DesignCoolingLoad = new DesignQuantity(load / points.Count, DesignUnit.Kilowatt),
                    RatedCoolingCapacity = null,
                    Position = Point(points[i]),
                    Orientation = previous?.Orientation ?? string.Empty,
                    State = previous?.State ?? DesignElementState.CreatedByPlugin
                });
            }
            foreach (DesignPipeRecord source in baseline.Pipes)
            {
                desired.Pipes.Add(CopyPipe(source));
                desiredRoom.PipeLogicalIds.Add(source.LogicalPipeId);
            }
            desired.Rooms.Add(desiredRoom);
            desired.Fingerprint = DesignSnapshotFingerprint.Compute(desired);
            return desired;
        }

        private static DesignSnapshot NewSnapshot(DesignSnapshotKind kind, DesignVersion version) => new DesignSnapshot
        {
            Kind = kind, Status = DesignSnapshotStatus.Valid, Version = CopyVersion(version),
            CapturedAtUtc = DateTime.UtcNow
        };
        private static DesignVersion CopyVersion(DesignVersion x) => new DesignVersion
        {
            SchemaVersion = x.SchemaVersion, Revision = x.Revision, RuleVersion = x.RuleVersion,
            EquipmentCatalogVersion = x.EquipmentCatalogVersion, HydraulicTableVersion = x.HydraulicTableVersion
        };
        private static DesignRoomRecord CopyRoom(DesignRoomRecord x) => new DesignRoomRecord
        {
            RoomUniqueId = x.RoomUniqueId, RoomNumber = x.RoomNumber, RoomName = x.RoomName,
            LevelUniqueId = x.LevelUniqueId, Length = x.Length, Height = x.Height,
            CoolingLoadDensity = x.CoolingLoadDensity, DesignCoolingLoad = x.DesignCoolingLoad,
            UnitCount = x.UnitCount, IsDetached = x.IsDetached
        };
        private static DesignDeviceRecord CopyDevice(DesignDeviceRecord x) => new DesignDeviceRecord
        {
            LogicalDeviceId = x.LogicalDeviceId, ElementUniqueId = x.ElementUniqueId,
            RoomUniqueId = x.RoomUniqueId, CatalogItemId = x.CatalogItemId,
            FamilySymbolUniqueId = x.FamilySymbolUniqueId, DesignCoolingLoad = x.DesignCoolingLoad,
            RatedCoolingCapacity = x.RatedCoolingCapacity, Position = x.Position,
            Orientation = x.Orientation, State = x.State
        };
        private static DesignPipeRecord CopyPipe(DesignPipeRecord x)
        {
            DesignPipeRecord copy = new DesignPipeRecord
            {
                LogicalPipeId = x.LogicalPipeId, ElementUniqueId = x.ElementUniqueId, Role = x.Role,
                Ownership = x.Ownership, SharedNetworkId = x.SharedNetworkId,
                ParentLogicalPipeId = x.ParentLogicalPipeId, CurrentDiameter = x.CurrentDiameter,
                PlannedDiameter = x.PlannedDiameter, State = x.State
            };
            foreach (string id in x.ServiceRoomUniqueIds) copy.ServiceRoomUniqueIds.Add(id);
            foreach (string id in x.ConnectedElementUniqueIds) copy.ConnectedElementUniqueIds.Add(id);
            return copy;
        }
        private static DesignPoint Point(XYZ p) => new DesignPoint(p.X * FEET_TO_MM / 1000.0,
            p.Y * FEET_TO_MM / 1000.0, p.Z * FEET_TO_MM / 1000.0, DesignUnit.Meter, "Revit internal origin");
        private static string Direction(XYZ p) => string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0:R},{1:R},{2:R}", p.X, p.Y, p.Z);
        private static DesignQuantity Diameter(Element element)
        {
            Pipe pipe = element as Pipe;
            return pipe == null ? null : new DesignQuantity(pipe.Diameter * FEET_TO_MM, DesignUnit.Millimeter);
        }
        private static FamilyInstance ResolveDoor(Document doc, Room room, FcuDesignOptions options)
        {
            ElementId id;
            return options.SelectedDoorIds != null && options.SelectedDoorIds.TryGetValue(room.Id.IntegerValue, out id)
                ? doc.GetElement(id) as FamilyInstance : RoomRuleSnapshotReader.ResolveDoorForRoom(doc, room);
        }
        private static RoomRuleSnapshot ReadDimensions(Document doc, Room room, FcuDesignOptions options)
        {
            RoomRuleSnapshot result = new RoomRuleSnapshotReader().Read(doc, room, ResolveDoor(doc, room, options));
            if (!result.IsValid) throw new InvalidOperationException(result.ErrorMessage);
            return result;
        }
        private static string ActionText(ReconciliationAction action) => action == ReconciliationAction.Create ? "新增"
            : action == ReconciliationAction.Update ? "更新" : action == ReconciliationAction.DeleteCandidate ? "删除候选"
            : action == ReconciliationAction.Conflict ? "冲突" : action == ReconciliationAction.Unmanaged ? "未管理" : "保留";
        private static string KindText(ReconciliationEntityKind kind) => kind == ReconciliationEntityKind.Room ? "房间"
            : kind == ReconciliationEntityKind.Device ? "设备" : "管线";
    }
}
