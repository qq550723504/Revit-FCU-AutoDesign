using System;
using System.Collections.Generic;

namespace FCUAutoDesign
{
    // These contracts deliberately contain no Revit or WPF types. Revit2020 adapters
    // can map them to Extensible Storage/DataStorage inside the owning transaction.
    public enum DesignUnit
    {
        Unknown = 0,
        Meter = 1,
        SquareMeter = 2,
        Kilowatt = 3,
        WattPerSquareMeter = 4,
        Millimeter = 5,
        Watt = 6,
        KilogramPerSecond = 7
    }

    public enum DesignRecordStatus
    {
        Draft = 0,
        Preview = 1,
        Committed = 2,
        Stale = 3,
        Conflict = 4,
        Detached = 5,
        Failed = 6
    }

    public enum DesignSnapshotKind
    {
        Baseline = 0,
        Current = 1,
        Desired = 2
    }

    public enum DesignSnapshotStatus
    {
        Captured = 0,
        Valid = 1,
        Stale = 2,
        Failed = 3
    }

    public enum DesignElementState
    {
        Managed = 0,
        CreatedByPlugin = 1,
        ModifiedByUser = 2,
        Missing = 3,
        DuplicateIdentity = 4,
        External = 5
    }

    public enum DesignPipeRole
    {
        SupplyBranch = 0,
        ReturnBranch = 1,
        CondensateBranch = 2,
        SharedMain = 3,
        SharedFitting = 4,
        Unknown = 5
    }

    public enum DesignPipeOwnership
    {
        Room = 0,
        SharedNetwork = 1,
        External = 2,
        Unknown = 3
    }

    public enum DesignIssueSeverity
    {
        Information = 0,
        Warning = 1,
        Error = 2
    }

    public enum DesignErrorCode
    {
        None = 0,
        InvalidIdentity = 1,
        UnsupportedSchema = 2,
        RevisionMismatch = 3,
        StalePreview = 4,
        DesignNotFound = 5,
        DuplicateDesignId = 6,
        DuplicateRoom = 7,
        DuplicateDeviceIdentity = 8,
        DuplicateElementIdentity = 9,
        ElementMissing = 10,
        DuplicateElementIdentityDetected = 11,
        UnownedElement = 12,
        SharedPipeOwnershipConflict = 13,
        SnapshotInvalid = 14,
        CommitFailed = 15,
        RepositoryUnavailable = 16,
        InvalidUnit = 17
    }

    public enum DesignOperationStatus
    {
        Succeeded = 0,
        NotFound = 1,
        Invalid = 2,
        Conflict = 3,
        Failed = 4
    }

    public sealed class DesignQuantity
    {
        public double Value { get; private set; }
        public DesignUnit Unit { get; private set; }

        public DesignQuantity(double value, DesignUnit unit)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException("value", "设计量必须是有限数值。");
            if (unit == DesignUnit.Unknown)
                throw new ArgumentException("设计量必须明确单位。", "unit");

            Value = value;
            Unit = unit;
        }
    }

    public sealed class DesignPoint
    {
        public double X { get; private set; }
        public double Y { get; private set; }
        public double Z { get; private set; }
        public DesignUnit Unit { get; private set; }
        public string CoordinateReference { get; private set; }

        public DesignPoint(double x, double y, double z, DesignUnit unit, string coordinateReference)
        {
            if (double.IsNaN(x) || double.IsInfinity(x)
                || double.IsNaN(y) || double.IsInfinity(y)
                || double.IsNaN(z) || double.IsInfinity(z))
                throw new ArgumentOutOfRangeException("x", "坐标必须是有限数值。");
            if (unit == DesignUnit.Unknown)
                throw new ArgumentException("坐标必须明确单位。", "unit");

            X = x;
            Y = y;
            Z = z;
            Unit = unit;
            CoordinateReference = coordinateReference ?? string.Empty;
        }
    }

    public sealed class DesignIdentity
    {
        // DesignId is a plugin-generated stable logical identity, not ElementId.
        public string DesignId { get; set; }
        // HostDocumentId must be stable across a close/reopen cycle.
        public string HostDocumentId { get; set; }
    }

    public sealed class DesignVersion
    {
        public int SchemaVersion { get; set; }
        public int Revision { get; set; }
        public string RuleVersion { get; set; }
        public string EquipmentCatalogVersion { get; set; }
        public string HydraulicTableVersion { get; set; }
    }

    public sealed class DesignIssue
    {
        public DesignIssueSeverity Severity { get; set; }
        public DesignErrorCode Code { get; set; }
        public string Message { get; set; }
        public string LogicalId { get; set; }
        public string ElementUniqueId { get; set; }
    }

    public sealed class DesignRoomRecord
    {
        public string RoomUniqueId { get; set; }
        public string RoomNumber { get; set; }
        public string RoomName { get; set; }
        public string LevelUniqueId { get; set; }
        public DesignQuantity Length { get; set; }
        public DesignQuantity Height { get; set; }
        public DesignQuantity CoolingLoadDensity { get; set; }
        public DesignQuantity DesignCoolingLoad { get; set; }
        public int UnitCount { get; set; }
        public bool IsDetached { get; set; }
        public IList<string> DeviceLogicalIds { get; private set; }
        public IList<string> PipeLogicalIds { get; private set; }

        public DesignRoomRecord()
        {
            DeviceLogicalIds = new List<string>();
            PipeLogicalIds = new List<string>();
        }
    }

    public sealed class DesignDeviceRecord
    {
        // LogicalDeviceId remains stable when the Revit element is replaced.
        public string LogicalDeviceId { get; set; }
        public string ElementUniqueId { get; set; }
        public string RoomUniqueId { get; set; }
        public string CatalogItemId { get; set; }
        public string FamilySymbolUniqueId { get; set; }
        public DesignQuantity DesignCoolingLoad { get; set; }
        public DesignQuantity RatedCoolingCapacity { get; set; }
        public DesignPoint Position { get; set; }
        public string Orientation { get; set; }
        public DesignElementState State { get; set; }
    }

    public sealed class DesignPipeRecord
    {
        public string LogicalPipeId { get; set; }
        public string ElementUniqueId { get; set; }
        public DesignPipeRole Role { get; set; }
        public DesignPipeOwnership Ownership { get; set; }
        // A shared main may serve many rooms and must not carry one room as owner.
        public string SharedNetworkId { get; set; }
        public string ParentLogicalPipeId { get; set; }
        public IList<string> ServiceRoomUniqueIds { get; private set; }
        public IList<string> ConnectedElementUniqueIds { get; private set; }
        public DesignQuantity CurrentDiameter { get; set; }
        public DesignQuantity PlannedDiameter { get; set; }
        public DesignElementState State { get; set; }

        public DesignPipeRecord()
        {
            ServiceRoomUniqueIds = new List<string>();
            ConnectedElementUniqueIds = new List<string>();
        }
    }

    public sealed class DesignSnapshot
    {
        public DesignSnapshotKind Kind { get; set; }
        public DesignSnapshotStatus Status { get; set; }
        public DesignVersion Version { get; set; }
        public DateTime CapturedAtUtc { get; set; }
        public string Fingerprint { get; set; }
        public IList<DesignRoomRecord> Rooms { get; private set; }
        public IList<DesignDeviceRecord> Devices { get; private set; }
        public IList<DesignPipeRecord> Pipes { get; private set; }
        public IList<DesignIssue> Issues { get; private set; }

        public DesignSnapshot()
        {
            Rooms = new List<DesignRoomRecord>();
            Devices = new List<DesignDeviceRecord>();
            Pipes = new List<DesignPipeRecord>();
            Issues = new List<DesignIssue>();
        }
    }

    public sealed class DesignRecord
    {
        public DesignIdentity Identity { get; set; }
        public DesignVersion Version { get; set; }
        public DesignRecordStatus Status { get; set; }
        // Only a successful committed state may become the next baseline.
        public DesignSnapshot LastSuccessfulSnapshot { get; set; }
        // Current and desired are preview data; the Revit adapter persists them only
        // if the product later approves a durable preview format.
        public DesignSnapshot CurrentSnapshot { get; set; }
        public DesignSnapshot DesiredSnapshot { get; set; }
    }

    public sealed class DesignRecordKey
    {
        public string DesignId { get; set; }
        public string HostDocumentId { get; set; }
    }

    public sealed class DesignWriteRequest
    {
        public DesignRecord Record { get; set; }
        public DesignVersion ExpectedVersion { get; set; }
    }

    public sealed class DesignOperationResult
    {
        public DesignOperationStatus Status { get; set; }
        public DesignErrorCode ErrorCode { get; set; }
        public string Message { get; set; }
        public DesignVersion Version { get; set; }
        public IList<DesignIssue> Issues { get; private set; }

        public DesignOperationResult()
        {
            Issues = new List<DesignIssue>();
        }
    }

    public sealed class DesignRecordReadResult
    {
        public DesignOperationStatus Status { get; set; }
        public DesignErrorCode ErrorCode { get; set; }
        public string Message { get; set; }
        public DesignRecord Record { get; set; }
        public IList<DesignIssue> Issues { get; private set; }

        public DesignRecordReadResult()
        {
            Issues = new List<DesignIssue>();
        }
    }

    // The implementation belongs in Revit2020/DesignRepository. This port keeps
    // persistence and transaction ownership out of the domain contract.
    public interface IDesignRecordRepository
    {
        DesignRecordReadResult Load(DesignRecordKey key);
        DesignOperationResult Save(DesignWriteRequest request);
    }
}
