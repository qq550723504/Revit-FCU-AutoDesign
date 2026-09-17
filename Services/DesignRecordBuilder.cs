using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using FCUAutoDesign.Business.EquipmentSelection;
using static FCUAutoDesign.RevitUnits;

namespace FCUAutoDesign
{
    internal sealed class DesignRecordBuilder
    {
        public DesignRecord Build(Document doc, Room room, FcuDesignResult design,
            FcuDesignOptions options, string designId)
        {
            if (doc == null || room == null || design == null || string.IsNullOrWhiteSpace(designId))
                throw new ArgumentException("创建设计记录所需参数不完整。");
            RoomRuleSnapshot dimensions = new RoomRuleSnapshotReader().Read(doc, room,
                ResolveSelectedDoor(doc, room, options));
            if (!dimensions.IsValid) throw new InvalidOperationException(dimensions.ErrorMessage);

            DesignVersion version = new DesignVersion
            {
                SchemaVersion = 1,
                Revision = 1,
                RuleVersion = "fcu-room-rule-v1",
                EquipmentCatalogVersion = "unconfirmed-preview-catalog",
                HydraulicTableVersion = "not-configured"
            };
            DesignSnapshot snapshot = new DesignSnapshot
            {
                Kind = DesignSnapshotKind.Baseline,
                Status = DesignSnapshotStatus.Valid,
                Version = version,
                CapturedAtUtc = DateTime.UtcNow
            };
            List<FcuDesignResult> devices = design.Devices.ToList();
            double designCoolingLoadKw = RoomLoadCalculator.DesignLoadKilowatts(
                dimensions.LengthM, dimensions.WidthM, options.CoolingIndexWPerSquareMeter);
            DesignRoomRecord roomRecord = new DesignRoomRecord
            {
                RoomUniqueId = room.UniqueId,
                RoomNumber = room.Number ?? string.Empty,
                RoomName = room.Name ?? string.Empty,
                LevelUniqueId = room.Level?.UniqueId ?? string.Empty,
                Length = new DesignQuantity(dimensions.LengthM, DesignUnit.Meter),
                Height = new DesignQuantity(dimensions.WidthM, DesignUnit.Meter),
                CoolingLoadDensity = new DesignQuantity(options.CoolingIndexWPerSquareMeter, DesignUnit.WattPerSquareMeter),
                DesignCoolingLoad = new DesignQuantity(designCoolingLoadKw, DesignUnit.Kilowatt),
                UnitCount = devices.Count,
                IsDetached = false
            };
            snapshot.Rooms.Add(roomRecord);

            Dictionary<string, DesignPipeRecord> pipes = new Dictionary<string, DesignPipeRecord>(StringComparer.Ordinal);
            for (int i = 0; i < devices.Count; i++)
            {
                FcuDesignResult unit = devices[i];
                FamilyInstance instance = doc.GetElement(unit.Outcome.FcuId) as FamilyInstance;
                LocationPoint location = instance?.Location as LocationPoint;
                if (instance == null || location == null)
                    throw new InvalidOperationException("无法读取已提交 FCU 的 UniqueId 或位置。");
                string logicalDeviceId = designId + ":device:" + (i + 1);
                roomRecord.DeviceLogicalIds.Add(logicalDeviceId);
                snapshot.Devices.Add(new DesignDeviceRecord
                {
                    LogicalDeviceId = logicalDeviceId,
                    ElementUniqueId = instance.UniqueId,
                    RoomUniqueId = room.UniqueId,
                    CatalogItemId = "unconfirmed:" + instance.Symbol.Name,
                    FamilySymbolUniqueId = instance.Symbol.UniqueId,
                    DesignCoolingLoad = new DesignQuantity(designCoolingLoadKw / Math.Max(1, devices.Count), DesignUnit.Kilowatt),
                    RatedCoolingCapacity = null,
                    Position = new DesignPoint(location.Point.X * FEET_TO_MM / 1000.0,
                        location.Point.Y * FEET_TO_MM / 1000.0,
                        location.Point.Z * FEET_TO_MM / 1000.0, DesignUnit.Meter, "Revit internal origin"),
                    Orientation = FormatDirection(unit.ExpectedOutletDirection),
                    State = DesignElementState.CreatedByPlugin
                });
                AddCircuit(doc, snapshot, roomRecord, pipes, unit.SupplyConnection,
                    DesignPipeRole.SupplyBranch, room.UniqueId, logicalDeviceId, unit.SupplyNetworkId);
                AddCircuit(doc, snapshot, roomRecord, pipes, unit.ReturnConnection,
                    DesignPipeRole.ReturnBranch, room.UniqueId, logicalDeviceId, unit.ReturnNetworkId);
                AddCircuit(doc, snapshot, roomRecord, pipes, unit.DrainConnection,
                    DesignPipeRole.CondensateBranch, room.UniqueId, logicalDeviceId, unit.CondensateNetworkId);
            }
            foreach (DesignPipeRecord pipe in pipes.Values) snapshot.Pipes.Add(pipe);
            snapshot.Fingerprint = DesignSnapshotFingerprint.Compute(snapshot);
            return new DesignRecord
            {
                Identity = new DesignIdentity
                {
                    DesignId = designId,
                    HostDocumentId = doc.ProjectInformation.UniqueId
                },
                Version = version,
                Status = DesignRecordStatus.Committed,
                LastSuccessfulSnapshot = snapshot
            };
        }

        private static FamilyInstance ResolveSelectedDoor(Document doc, Room room, FcuDesignOptions options)
        {
            ElementId id;
            return options.SelectedDoorIds != null
                && options.SelectedDoorIds.TryGetValue(room.Id.IntegerValue, out id)
                ? doc.GetElement(id) as FamilyInstance
                : RoomRuleSnapshotReader.ResolveDoorForRoom(doc, room);
        }

        private static void AddCircuit(Document doc, DesignSnapshot snapshot, DesignRoomRecord room,
            IDictionary<string, DesignPipeRecord> records, TeeConnectionResult circuit,
            DesignPipeRole role, string roomUniqueId, string logicalDeviceId, string sharedNetworkId)
        {
            if (circuit == null || !circuit.BranchCreated) return;
            List<ElementId> owned = circuit.Chain.Where(x => x != null).ToList();
            if (circuit.FirstPipeId != null && !owned.Contains(circuit.FirstPipeId)) owned.Add(circuit.FirstPipeId);
            foreach (ElementId id in owned) AddPipe(doc, room, records, id, role,
                DesignPipeOwnership.Room, roomUniqueId, logicalDeviceId, null);
            foreach (ElementId id in new[] { circuit.MainPart1Id, circuit.MainPart2Id }.Where(x => x != null))
                AddPipe(doc, room, records, id, DesignPipeRole.SharedMain,
                    DesignPipeOwnership.SharedNetwork, roomUniqueId, logicalDeviceId, sharedNetworkId);
        }

        private static void AddPipe(Document doc, DesignRoomRecord room,
            IDictionary<string, DesignPipeRecord> records, ElementId id, DesignPipeRole role,
            DesignPipeOwnership ownership, string roomUniqueId, string connectedId, string sharedNetworkId)
        {
            Element element = doc.GetElement(id);
            if (element == null || string.IsNullOrWhiteSpace(element.UniqueId)) return;
            DesignPipeRecord record;
            if (!records.TryGetValue(element.UniqueId, out record))
            {
                record = new DesignPipeRecord
                {
                    LogicalPipeId = "element:" + element.UniqueId,
                    ElementUniqueId = element.UniqueId,
                    Role = role,
                    Ownership = ownership,
                    SharedNetworkId = sharedNetworkId,
                    State = ownership == DesignPipeOwnership.Room
                        ? DesignElementState.CreatedByPlugin : DesignElementState.External
                };
                records.Add(element.UniqueId, record);
                room.PipeLogicalIds.Add(record.LogicalPipeId);
            }
            if (!record.ServiceRoomUniqueIds.Contains(roomUniqueId)) record.ServiceRoomUniqueIds.Add(roomUniqueId);
            if (!record.ConnectedElementUniqueIds.Contains(connectedId)) record.ConnectedElementUniqueIds.Add(connectedId);
        }

        private static string FormatDirection(XYZ direction)
        {
            return direction == null ? string.Empty
                : string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:R},{1:R},{2:R}", direction.X, direction.Y, direction.Z);
        }

    }
}
