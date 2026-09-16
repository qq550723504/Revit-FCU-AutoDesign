using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace FCUAutoDesign
{
    internal sealed class DesignRecordPayloadCodec
    {
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public string Encode(DesignRecord record)
        {
            if (record == null) throw new ArgumentNullException("record");
            return serializer.Serialize(ToStored(record));
        }

        public DesignRecord Decode(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("设计记录内容为空。", "json");
            StoredRecord stored = serializer.Deserialize<StoredRecord>(json);
            if (stored == null) throw new InvalidOperationException("设计记录内容无法反序列化。");
            return FromStored(stored);
        }

        private static StoredRecord ToStored(DesignRecord source)
        {
            return new StoredRecord
            {
                Identity = source.Identity == null ? null : new StoredIdentity
                {
                    DesignId = source.Identity.DesignId, HostDocumentId = source.Identity.HostDocumentId
                },
                Version = ToStored(source.Version), Status = (int)source.Status,
                LastSuccessfulSnapshot = ToStored(source.LastSuccessfulSnapshot),
                CurrentSnapshot = ToStored(source.CurrentSnapshot), DesiredSnapshot = ToStored(source.DesiredSnapshot)
            };
        }

        private static DesignRecord FromStored(StoredRecord source)
        {
            return new DesignRecord
            {
                Identity = source.Identity == null ? null : new DesignIdentity
                {
                    DesignId = source.Identity.DesignId, HostDocumentId = source.Identity.HostDocumentId
                },
                Version = FromStored(source.Version), Status = (DesignRecordStatus)source.Status,
                LastSuccessfulSnapshot = FromStored(source.LastSuccessfulSnapshot),
                CurrentSnapshot = FromStored(source.CurrentSnapshot), DesiredSnapshot = FromStored(source.DesiredSnapshot)
            };
        }

        private static StoredVersion ToStored(DesignVersion value) => value == null ? null : new StoredVersion
        {
            SchemaVersion = value.SchemaVersion, Revision = value.Revision, RuleVersion = value.RuleVersion,
            EquipmentCatalogVersion = value.EquipmentCatalogVersion, HydraulicTableVersion = value.HydraulicTableVersion
        };

        private static DesignVersion FromStored(StoredVersion value) => value == null ? null : new DesignVersion
        {
            SchemaVersion = value.SchemaVersion, Revision = value.Revision, RuleVersion = value.RuleVersion,
            EquipmentCatalogVersion = value.EquipmentCatalogVersion, HydraulicTableVersion = value.HydraulicTableVersion
        };

        private static StoredQuantity ToStored(DesignQuantity value) => value == null ? null : new StoredQuantity
        { Value = value.Value, Unit = (int)value.Unit };

        private static DesignQuantity FromStored(StoredQuantity value) => value == null ? null
            : new DesignQuantity(value.Value, (DesignUnit)value.Unit);

        private static StoredPoint ToStored(DesignPoint value) => value == null ? null : new StoredPoint
        { X = value.X, Y = value.Y, Z = value.Z, Unit = (int)value.Unit, CoordinateReference = value.CoordinateReference };

        private static DesignPoint FromStored(StoredPoint value) => value == null ? null
            : new DesignPoint(value.X, value.Y, value.Z, (DesignUnit)value.Unit, value.CoordinateReference);

        private static StoredSnapshot ToStored(DesignSnapshot source)
        {
            if (source == null) return null;
            return new StoredSnapshot
            {
                Kind = (int)source.Kind, Status = (int)source.Status, Version = ToStored(source.Version),
                CapturedAtUtc = source.CapturedAtUtc.ToString("o"), Fingerprint = source.Fingerprint,
                Rooms = source.Rooms.Select(x => new StoredRoom
                {
                    RoomUniqueId = x.RoomUniqueId, RoomNumber = x.RoomNumber, RoomName = x.RoomName,
                    LevelUniqueId = x.LevelUniqueId, Length = ToStored(x.Length), Height = ToStored(x.Height),
                    CoolingLoadDensity = ToStored(x.CoolingLoadDensity), DesignCoolingLoad = ToStored(x.DesignCoolingLoad),
                    UnitCount = x.UnitCount, IsDetached = x.IsDetached,
                    DeviceLogicalIds = x.DeviceLogicalIds.ToList(), PipeLogicalIds = x.PipeLogicalIds.ToList()
                }).ToList(),
                Devices = source.Devices.Select(x => new StoredDevice
                {
                    LogicalDeviceId = x.LogicalDeviceId, ElementUniqueId = x.ElementUniqueId,
                    RoomUniqueId = x.RoomUniqueId, CatalogItemId = x.CatalogItemId,
                    FamilySymbolUniqueId = x.FamilySymbolUniqueId, DesignCoolingLoad = ToStored(x.DesignCoolingLoad),
                    RatedCoolingCapacity = ToStored(x.RatedCoolingCapacity), Position = ToStored(x.Position),
                    Orientation = x.Orientation, State = (int)x.State
                }).ToList(),
                Pipes = source.Pipes.Select(x => new StoredPipe
                {
                    LogicalPipeId = x.LogicalPipeId, ElementUniqueId = x.ElementUniqueId, Role = (int)x.Role,
                    Ownership = (int)x.Ownership, SharedNetworkId = x.SharedNetworkId,
                    ParentLogicalPipeId = x.ParentLogicalPipeId,
                    ServiceRoomUniqueIds = x.ServiceRoomUniqueIds.ToList(),
                    ConnectedElementUniqueIds = x.ConnectedElementUniqueIds.ToList(),
                    CurrentDiameter = ToStored(x.CurrentDiameter), PlannedDiameter = ToStored(x.PlannedDiameter),
                    State = (int)x.State
                }).ToList(),
                Issues = source.Issues.Select(x => new StoredIssue
                {
                    Severity = (int)x.Severity, Code = (int)x.Code, Message = x.Message,
                    LogicalId = x.LogicalId, ElementUniqueId = x.ElementUniqueId
                }).ToList()
            };
        }

        private static DesignSnapshot FromStored(StoredSnapshot source)
        {
            if (source == null) return null;
            DateTime captured;
            if (!DateTime.TryParse(source.CapturedAtUtc, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out captured))
                throw new InvalidOperationException("设计快照时间格式无效。");
            DesignSnapshot target = new DesignSnapshot
            {
                Kind = (DesignSnapshotKind)source.Kind, Status = (DesignSnapshotStatus)source.Status,
                Version = FromStored(source.Version), CapturedAtUtc = captured, Fingerprint = source.Fingerprint
            };
            foreach (StoredRoom x in source.Rooms ?? new List<StoredRoom>())
            {
                DesignRoomRecord room = new DesignRoomRecord
                {
                    RoomUniqueId = x.RoomUniqueId, RoomNumber = x.RoomNumber, RoomName = x.RoomName,
                    LevelUniqueId = x.LevelUniqueId, Length = FromStored(x.Length), Height = FromStored(x.Height),
                    CoolingLoadDensity = FromStored(x.CoolingLoadDensity), DesignCoolingLoad = FromStored(x.DesignCoolingLoad),
                    UnitCount = x.UnitCount, IsDetached = x.IsDetached
                };
                foreach (string id in x.DeviceLogicalIds ?? new List<string>()) room.DeviceLogicalIds.Add(id);
                foreach (string id in x.PipeLogicalIds ?? new List<string>()) room.PipeLogicalIds.Add(id);
                target.Rooms.Add(room);
            }
            foreach (StoredDevice x in source.Devices ?? new List<StoredDevice>())
                target.Devices.Add(new DesignDeviceRecord
                {
                    LogicalDeviceId = x.LogicalDeviceId, ElementUniqueId = x.ElementUniqueId,
                    RoomUniqueId = x.RoomUniqueId, CatalogItemId = x.CatalogItemId,
                    FamilySymbolUniqueId = x.FamilySymbolUniqueId, DesignCoolingLoad = FromStored(x.DesignCoolingLoad),
                    RatedCoolingCapacity = FromStored(x.RatedCoolingCapacity), Position = FromStored(x.Position),
                    Orientation = x.Orientation, State = (DesignElementState)x.State
                });
            foreach (StoredPipe x in source.Pipes ?? new List<StoredPipe>())
            {
                DesignPipeRecord pipe = new DesignPipeRecord
                {
                    LogicalPipeId = x.LogicalPipeId, ElementUniqueId = x.ElementUniqueId,
                    Role = (DesignPipeRole)x.Role, Ownership = (DesignPipeOwnership)x.Ownership,
                    SharedNetworkId = x.SharedNetworkId, ParentLogicalPipeId = x.ParentLogicalPipeId,
                    CurrentDiameter = FromStored(x.CurrentDiameter), PlannedDiameter = FromStored(x.PlannedDiameter),
                    State = (DesignElementState)x.State
                };
                foreach (string id in x.ServiceRoomUniqueIds ?? new List<string>()) pipe.ServiceRoomUniqueIds.Add(id);
                foreach (string id in x.ConnectedElementUniqueIds ?? new List<string>()) pipe.ConnectedElementUniqueIds.Add(id);
                target.Pipes.Add(pipe);
            }
            foreach (StoredIssue x in source.Issues ?? new List<StoredIssue>())
                target.Issues.Add(new DesignIssue
                {
                    Severity = (DesignIssueSeverity)x.Severity, Code = (DesignErrorCode)x.Code,
                    Message = x.Message, LogicalId = x.LogicalId, ElementUniqueId = x.ElementUniqueId
                });
            return target;
        }

        private sealed class StoredRecord { public StoredIdentity Identity { get; set; } public StoredVersion Version { get; set; } public int Status { get; set; } public StoredSnapshot LastSuccessfulSnapshot { get; set; } public StoredSnapshot CurrentSnapshot { get; set; } public StoredSnapshot DesiredSnapshot { get; set; } }
        private sealed class StoredIdentity { public string DesignId { get; set; } public string HostDocumentId { get; set; } }
        private sealed class StoredVersion { public int SchemaVersion { get; set; } public int Revision { get; set; } public string RuleVersion { get; set; } public string EquipmentCatalogVersion { get; set; } public string HydraulicTableVersion { get; set; } }
        private sealed class StoredQuantity { public double Value { get; set; } public int Unit { get; set; } }
        private sealed class StoredPoint { public double X { get; set; } public double Y { get; set; } public double Z { get; set; } public int Unit { get; set; } public string CoordinateReference { get; set; } }
        private sealed class StoredSnapshot { public int Kind { get; set; } public int Status { get; set; } public StoredVersion Version { get; set; } public string CapturedAtUtc { get; set; } public string Fingerprint { get; set; } public List<StoredRoom> Rooms { get; set; } public List<StoredDevice> Devices { get; set; } public List<StoredPipe> Pipes { get; set; } public List<StoredIssue> Issues { get; set; } }
        private sealed class StoredRoom { public string RoomUniqueId { get; set; } public string RoomNumber { get; set; } public string RoomName { get; set; } public string LevelUniqueId { get; set; } public StoredQuantity Length { get; set; } public StoredQuantity Height { get; set; } public StoredQuantity CoolingLoadDensity { get; set; } public StoredQuantity DesignCoolingLoad { get; set; } public int UnitCount { get; set; } public bool IsDetached { get; set; } public List<string> DeviceLogicalIds { get; set; } public List<string> PipeLogicalIds { get; set; } }
        private sealed class StoredDevice { public string LogicalDeviceId { get; set; } public string ElementUniqueId { get; set; } public string RoomUniqueId { get; set; } public string CatalogItemId { get; set; } public string FamilySymbolUniqueId { get; set; } public StoredQuantity DesignCoolingLoad { get; set; } public StoredQuantity RatedCoolingCapacity { get; set; } public StoredPoint Position { get; set; } public string Orientation { get; set; } public int State { get; set; } }
        private sealed class StoredPipe { public string LogicalPipeId { get; set; } public string ElementUniqueId { get; set; } public int Role { get; set; } public int Ownership { get; set; } public string SharedNetworkId { get; set; } public string ParentLogicalPipeId { get; set; } public List<string> ServiceRoomUniqueIds { get; set; } public List<string> ConnectedElementUniqueIds { get; set; } public StoredQuantity CurrentDiameter { get; set; } public StoredQuantity PlannedDiameter { get; set; } public int State { get; set; } }
        private sealed class StoredIssue { public int Severity { get; set; } public int Code { get; set; } public string Message { get; set; } public string LogicalId { get; set; } public string ElementUniqueId { get; set; } }
    }
}
