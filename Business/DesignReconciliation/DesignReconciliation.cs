using System;
using System.Collections.Generic;
using System.Linq;

namespace FCUAutoDesign.Business.DesignReconciliation
{
    public enum ReconciliationAction
    {
        NoChange,
        Create,
        Update,
        DeleteCandidate,
        Conflict,
        Unmanaged
    }

    public enum ReconciliationEntityKind
    {
        Room,
        Device,
        Pipe
    }

    public sealed class ReconciliationRequest
    {
        public DesignSnapshot Baseline { get; set; }
        public DesignSnapshot Current { get; set; }
        public DesignSnapshot Desired { get; set; }
        public int? ExpectedCurrentRevision { get; set; }
        public string ExpectedCurrentFingerprint { get; set; }
    }

    public sealed class ReconciliationItem
    {
        public ReconciliationEntityKind EntityKind { get; internal set; }
        public string LogicalId { get; internal set; }
        public ReconciliationAction Action { get; internal set; }
        public IList<string> ChangedFields { get; internal set; }
        public string Message { get; internal set; }

        public ReconciliationItem()
        {
            ChangedFields = new List<string>();
        }
    }

    public sealed class ReconciliationPlan
    {
        public bool IsApplicable { get; internal set; }
        public DesignErrorCode ErrorCode { get; internal set; }
        public string ErrorMessage { get; internal set; }
        public IList<ReconciliationItem> Items { get; private set; }

        public ReconciliationPlan()
        {
            Items = new List<ReconciliationItem>();
        }
    }

    public sealed class DesignReconciliationPlanner
    {
        private const double Epsilon = 1e-9;

        public ReconciliationPlan BuildPlan(ReconciliationRequest request)
        {
            ReconciliationPlan plan = new ReconciliationPlan();
            if (request == null || request.Baseline == null || request.Current == null || request.Desired == null)
                return Fail(plan, DesignErrorCode.SnapshotInvalid, "三方快照必须全部存在。");
            if (!ValidSnapshot(request.Baseline, DesignSnapshotKind.Baseline)
                || !ValidSnapshot(request.Current, DesignSnapshotKind.Current)
                || !ValidSnapshot(request.Desired, DesignSnapshotKind.Desired))
                return Fail(plan, DesignErrorCode.SnapshotInvalid, "三方快照类型或状态无效。");

            if (request.ExpectedCurrentRevision.HasValue
                && (request.Current.Version == null
                    || request.Current.Version.Revision != request.ExpectedCurrentRevision.Value))
                return Fail(plan, DesignErrorCode.StalePreview, "当前模型修订号已变化，旧预览失效。");
            if (!string.IsNullOrWhiteSpace(request.ExpectedCurrentFingerprint)
                && !string.Equals(request.ExpectedCurrentFingerprint, request.Current.Fingerprint,
                    StringComparison.Ordinal))
                return Fail(plan, DesignErrorCode.StalePreview, "当前模型指纹已变化，旧预览失效。");

            Dictionary<string, DesignRoomRecord> baselineRooms;
            Dictionary<string, DesignRoomRecord> currentRooms;
            Dictionary<string, DesignRoomRecord> desiredRooms;
            Dictionary<string, DesignDeviceRecord> baselineDevices;
            Dictionary<string, DesignDeviceRecord> currentDevices;
            Dictionary<string, DesignDeviceRecord> desiredDevices;
            Dictionary<string, DesignPipeRecord> baselinePipes;
            Dictionary<string, DesignPipeRecord> currentPipes;
            Dictionary<string, DesignPipeRecord> desiredPipes;
            if (!TryIndex(request.Baseline, plan, out baselineRooms, out baselineDevices, out baselinePipes)
                || !TryIndex(request.Current, plan, out currentRooms, out currentDevices, out currentPipes)
                || !TryIndex(request.Desired, plan, out desiredRooms, out desiredDevices, out desiredPipes))
                return plan;

            CompareRooms(plan, baselineRooms, currentRooms, desiredRooms);
            CompareDevices(plan, baselineDevices, currentDevices, desiredDevices);
            ComparePipes(plan, baselinePipes, currentPipes, desiredPipes);
            plan.IsApplicable = !plan.Items.Any(x => x.Action == ReconciliationAction.Conflict);
            return plan;
        }

        private static bool ValidSnapshot(DesignSnapshot snapshot, DesignSnapshotKind expectedKind)
        {
            return snapshot.Kind == expectedKind && snapshot.Status != DesignSnapshotStatus.Failed;
        }

        private static bool TryIndex(
            DesignSnapshot snapshot,
            ReconciliationPlan plan,
            out Dictionary<string, DesignRoomRecord> rooms,
            out Dictionary<string, DesignDeviceRecord> devices,
            out Dictionary<string, DesignPipeRecord> pipes)
        {
            rooms = new Dictionary<string, DesignRoomRecord>(StringComparer.Ordinal);
            devices = new Dictionary<string, DesignDeviceRecord>(StringComparer.Ordinal);
            pipes = new Dictionary<string, DesignPipeRecord>(StringComparer.Ordinal);
            if (snapshot.Rooms == null || snapshot.Devices == null || snapshot.Pipes == null)
                return FailIndex(plan, DesignErrorCode.SnapshotInvalid, "快照缺少房间、设备或管线集合。");

            foreach (DesignRoomRecord room in snapshot.Rooms)
            {
                if (room == null || string.IsNullOrWhiteSpace(room.RoomUniqueId)
                    || rooms.ContainsKey(room.RoomUniqueId))
                    return FailIndex(plan, DesignErrorCode.DuplicateRoom, "快照包含重复或空房间身份。");
                rooms.Add(room.RoomUniqueId, room);
            }
            foreach (DesignDeviceRecord device in snapshot.Devices)
            {
                if (device == null || string.IsNullOrWhiteSpace(device.LogicalDeviceId)
                    || devices.ContainsKey(device.LogicalDeviceId))
                    return FailIndex(plan, DesignErrorCode.DuplicateDeviceIdentity,
                        "快照包含重复或空设备逻辑身份。");
                devices.Add(device.LogicalDeviceId, device);
            }
            foreach (DesignPipeRecord pipe in snapshot.Pipes)
            {
                if (pipe == null || string.IsNullOrWhiteSpace(pipe.LogicalPipeId)
                    || pipes.ContainsKey(pipe.LogicalPipeId))
                    return FailIndex(plan, DesignErrorCode.DuplicateElementIdentity,
                        "快照包含重复或空管线逻辑身份。");
                pipes.Add(pipe.LogicalPipeId, pipe);
            }
            return true;
        }

        private static void CompareRooms(
            ReconciliationPlan plan,
            IDictionary<string, DesignRoomRecord> baseline,
            IDictionary<string, DesignRoomRecord> current,
            IDictionary<string, DesignRoomRecord> desired)
        {
            foreach (string id in UnionKeys(baseline, current, desired))
            {
                DesignRoomRecord b = Get(baseline, id);
                DesignRoomRecord c = Get(current, id);
                DesignRoomRecord d = Get(desired, id);
                AddEntityPlan(plan, ReconciliationEntityKind.Room, id,
                    DiffRoom(b, c), DiffRoom(b, d), ConflictingRoomFields(b, c, d),
                    b != null, c != null, d != null,
                    "房间");
            }
        }

        private static void CompareDevices(
            ReconciliationPlan plan,
            IDictionary<string, DesignDeviceRecord> baseline,
            IDictionary<string, DesignDeviceRecord> current,
            IDictionary<string, DesignDeviceRecord> desired)
        {
            foreach (string id in UnionKeys(baseline, current, desired))
            {
                DesignDeviceRecord b = Get(baseline, id);
                DesignDeviceRecord c = Get(current, id);
                DesignDeviceRecord d = Get(desired, id);
                AddEntityPlan(plan, ReconciliationEntityKind.Device, id,
                    DiffDevice(b, c), DiffDevice(b, d), ConflictingDeviceFields(b, c, d),
                    b != null, c != null, d != null,
                    "设备");
            }
        }

        private static void ComparePipes(
            ReconciliationPlan plan,
            IDictionary<string, DesignPipeRecord> baseline,
            IDictionary<string, DesignPipeRecord> current,
            IDictionary<string, DesignPipeRecord> desired)
        {
            foreach (string id in UnionKeys(baseline, current, desired))
            {
                DesignPipeRecord b = Get(baseline, id);
                DesignPipeRecord c = Get(current, id);
                DesignPipeRecord d = Get(desired, id);
                AddEntityPlan(plan, ReconciliationEntityKind.Pipe, id,
                    DiffPipe(b, c), DiffPipe(b, d), ConflictingPipeFields(b, c, d),
                    b != null, c != null, d != null,
                    "管线");
            }
        }

        private static void AddEntityPlan(
            ReconciliationPlan plan,
            ReconciliationEntityKind kind,
            string id,
            IList<string> currentChanges,
            IList<string> desiredChanges,
            IList<string> conflictingFields,
            bool inBaseline,
            bool inCurrent,
            bool inDesired,
            string label)
        {
            ReconciliationItem item = new ReconciliationItem { EntityKind = kind, LogicalId = id };
            if (!inBaseline && !inCurrent && inDesired)
            {
                item.Action = ReconciliationAction.Create;
                item.Message = label + "是新的期望对象。";
            }
            else if (inBaseline && !inCurrent && inDesired)
            {
                item.Action = ReconciliationAction.Conflict;
                item.Message = label + "已从当前模型消失，不自动恢复。";
                item.ChangedFields.Add("ElementMissing");
            }
            else if (inBaseline && inCurrent && !inDesired)
            {
                item.Action = ReconciliationAction.DeleteCandidate;
                item.Message = label + "不再属于期望方案，删除必须单独确认。";
            }
            else if (!inBaseline && inCurrent && !inDesired)
            {
                item.Action = ReconciliationAction.Unmanaged;
                item.Message = label + "没有设计基线，不自动认领或删除。";
            }
            else if (!inBaseline && inCurrent && inDesired)
            {
                item.Action = ReconciliationAction.Conflict;
                item.Message = label + "当前已存在但没有设计基线，不自动认领。";
                item.ChangedFields.Add("UnownedElement");
            }
            else
            {
                item.ChangedFields = MergeChanges(currentChanges, desiredChanges);
                List<string> conflicts = conflictingFields.ToList();
                bool hasDesiredOnly = desiredChanges.Except(currentChanges, StringComparer.Ordinal).Any();
                if (conflicts.Count > 0)
                {
                    item.Action = ReconciliationAction.Conflict;
                    item.Message = label + "存在人工与算法同时修改的字段。";
                    item.ChangedFields = conflicts;
                }
                else if (hasDesiredOnly)
                {
                    item.Action = ReconciliationAction.Update;
                    item.Message = currentChanges.Count == 0
                        ? label + "按期望方案更新。"
                        : label + "更新算法字段并保留人工修改字段。";
                }
                else
                {
                    item.Action = ReconciliationAction.NoChange;
                    item.Message = currentChanges.Count == 0
                        ? label + "与期望方案一致。"
                        : label + "仅存在人工修改，默认保留。";
                }
            }
            if (item.Action != ReconciliationAction.NoChange || item.ChangedFields.Count > 0)
                plan.Items.Add(item);
        }

        private static IList<string> DiffRoom(DesignRoomRecord before, DesignRoomRecord after)
        {
            List<string> result = new List<string>();
            if (before == null || after == null) return result;
            if (!Equal(before.RoomNumber, after.RoomNumber)) result.Add("RoomNumber");
            if (!Equal(before.RoomName, after.RoomName)) result.Add("RoomName");
            if (!Equal(before.LevelUniqueId, after.LevelUniqueId)) result.Add("LevelUniqueId");
            if (!Equal(before.Length, after.Length)) result.Add("Length");
            if (!Equal(before.Height, after.Height)) result.Add("Height");
            if (!Equal(before.CoolingLoadDensity, after.CoolingLoadDensity)) result.Add("CoolingLoadDensity");
            if (!Equal(before.DesignCoolingLoad, after.DesignCoolingLoad)) result.Add("DesignCoolingLoad");
            if (before.UnitCount != after.UnitCount) result.Add("UnitCount");
            if (!Equal(before.DeviceLogicalIds, after.DeviceLogicalIds)) result.Add("DeviceLogicalIds");
            if (!Equal(before.PipeLogicalIds, after.PipeLogicalIds)) result.Add("PipeLogicalIds");
            return result;
        }

        private static IList<string> DiffDevice(DesignDeviceRecord before, DesignDeviceRecord after)
        {
            List<string> result = new List<string>();
            if (before == null || after == null) return result;
            if (!Equal(before.ElementUniqueId, after.ElementUniqueId)) result.Add("ElementUniqueId");
            if (!Equal(before.RoomUniqueId, after.RoomUniqueId)) result.Add("RoomUniqueId");
            if (!Equal(before.CatalogItemId, after.CatalogItemId)) result.Add("CatalogItemId");
            if (!Equal(before.FamilySymbolUniqueId, after.FamilySymbolUniqueId)) result.Add("FamilySymbolUniqueId");
            if (!Equal(before.DesignCoolingLoad, after.DesignCoolingLoad)) result.Add("DesignCoolingLoad");
            if (!Equal(before.RatedCoolingCapacity, after.RatedCoolingCapacity)) result.Add("RatedCoolingCapacity");
            if (!Equal(before.Position, after.Position)) result.Add("Position");
            if (!Equal(before.Orientation, after.Orientation)) result.Add("Orientation");
            if (before.State != after.State) result.Add("State");
            return result;
        }

        private static IList<string> DiffPipe(DesignPipeRecord before, DesignPipeRecord after)
        {
            List<string> result = new List<string>();
            if (before == null || after == null) return result;
            if (!Equal(before.ElementUniqueId, after.ElementUniqueId)) result.Add("ElementUniqueId");
            if (before.Role != after.Role) result.Add("Role");
            if (before.Ownership != after.Ownership) result.Add("Ownership");
            if (!Equal(before.SharedNetworkId, after.SharedNetworkId)) result.Add("SharedNetworkId");
            if (!Equal(before.ParentLogicalPipeId, after.ParentLogicalPipeId)) result.Add("ParentLogicalPipeId");
            if (!Equal(before.ServiceRoomUniqueIds, after.ServiceRoomUniqueIds)) result.Add("ServiceRoomUniqueIds");
            if (!Equal(before.ConnectedElementUniqueIds, after.ConnectedElementUniqueIds)) result.Add("ConnectedElementUniqueIds");
            if (!Equal(before.CurrentDiameter, after.CurrentDiameter)) result.Add("CurrentDiameter");
            if (!Equal(before.PlannedDiameter, after.PlannedDiameter)) result.Add("PlannedDiameter");
            if (before.State != after.State) result.Add("State");
            return result;
        }

        private static IList<string> ConflictingRoomFields(
            DesignRoomRecord baseline, DesignRoomRecord current, DesignRoomRecord desired)
        {
            return ConflictingFields(DiffRoom(baseline, current), DiffRoom(baseline, desired),
                field => EqualRoomField(field, current, desired));
        }

        private static IList<string> ConflictingDeviceFields(
            DesignDeviceRecord baseline, DesignDeviceRecord current, DesignDeviceRecord desired)
        {
            return ConflictingFields(DiffDevice(baseline, current), DiffDevice(baseline, desired),
                field => EqualDeviceField(field, current, desired));
        }

        private static IList<string> ConflictingPipeFields(
            DesignPipeRecord baseline, DesignPipeRecord current, DesignPipeRecord desired)
        {
            return ConflictingFields(DiffPipe(baseline, current), DiffPipe(baseline, desired),
                field => EqualPipeField(field, current, desired));
        }

        private static IList<string> ConflictingFields(
            IList<string> currentChanges, IList<string> desiredChanges, Func<string, bool> equalCurrentDesired)
        {
            return currentChanges.Intersect(desiredChanges, StringComparer.Ordinal)
                .Where(field => !equalCurrentDesired(field)).ToList();
        }

        private static bool EqualRoomField(string field, DesignRoomRecord current, DesignRoomRecord desired)
        {
            switch (field)
            {
                case "RoomNumber": return Equal(current.RoomNumber, desired.RoomNumber);
                case "RoomName": return Equal(current.RoomName, desired.RoomName);
                case "LevelUniqueId": return Equal(current.LevelUniqueId, desired.LevelUniqueId);
                case "Length": return Equal(current.Length, desired.Length);
                case "Height": return Equal(current.Height, desired.Height);
                case "CoolingLoadDensity": return Equal(current.CoolingLoadDensity, desired.CoolingLoadDensity);
                case "DesignCoolingLoad": return Equal(current.DesignCoolingLoad, desired.DesignCoolingLoad);
                case "UnitCount": return current.UnitCount == desired.UnitCount;
                case "DeviceLogicalIds": return Equal(current.DeviceLogicalIds, desired.DeviceLogicalIds);
                case "PipeLogicalIds": return Equal(current.PipeLogicalIds, desired.PipeLogicalIds);
                default: return false;
            }
        }

        private static bool EqualDeviceField(string field, DesignDeviceRecord current, DesignDeviceRecord desired)
        {
            switch (field)
            {
                case "ElementUniqueId": return Equal(current.ElementUniqueId, desired.ElementUniqueId);
                case "RoomUniqueId": return Equal(current.RoomUniqueId, desired.RoomUniqueId);
                case "CatalogItemId": return Equal(current.CatalogItemId, desired.CatalogItemId);
                case "FamilySymbolUniqueId": return Equal(current.FamilySymbolUniqueId, desired.FamilySymbolUniqueId);
                case "DesignCoolingLoad": return Equal(current.DesignCoolingLoad, desired.DesignCoolingLoad);
                case "RatedCoolingCapacity": return Equal(current.RatedCoolingCapacity, desired.RatedCoolingCapacity);
                case "Position": return Equal(current.Position, desired.Position);
                case "Orientation": return Equal(current.Orientation, desired.Orientation);
                case "State": return current.State == desired.State;
                default: return false;
            }
        }

        private static bool EqualPipeField(string field, DesignPipeRecord current, DesignPipeRecord desired)
        {
            switch (field)
            {
                case "ElementUniqueId": return Equal(current.ElementUniqueId, desired.ElementUniqueId);
                case "Role": return current.Role == desired.Role;
                case "Ownership": return current.Ownership == desired.Ownership;
                case "SharedNetworkId": return Equal(current.SharedNetworkId, desired.SharedNetworkId);
                case "ParentLogicalPipeId": return Equal(current.ParentLogicalPipeId, desired.ParentLogicalPipeId);
                case "ServiceRoomUniqueIds": return Equal(current.ServiceRoomUniqueIds, desired.ServiceRoomUniqueIds);
                case "ConnectedElementUniqueIds": return Equal(current.ConnectedElementUniqueIds, desired.ConnectedElementUniqueIds);
                case "CurrentDiameter": return Equal(current.CurrentDiameter, desired.CurrentDiameter);
                case "PlannedDiameter": return Equal(current.PlannedDiameter, desired.PlannedDiameter);
                case "State": return current.State == desired.State;
                default: return false;
            }
        }

        private static bool Equal(DesignQuantity left, DesignQuantity right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null) return false;
            return left.Unit == right.Unit && Math.Abs(left.Value - right.Value) <= Epsilon;
        }

        private static bool Equal(DesignPoint left, DesignPoint right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null) return false;
            return left.Unit == right.Unit
                && Math.Abs(left.X - right.X) <= Epsilon
                && Math.Abs(left.Y - right.Y) <= Epsilon
                && Math.Abs(left.Z - right.Z) <= Epsilon
                && Equal(left.CoordinateReference, right.CoordinateReference);
        }

        private static bool Equal(string left, string right)
        {
            return string.Equals(left, right, StringComparison.Ordinal);
        }

        private static bool Equal<T>(IList<T> left, IList<T> right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Count != right.Count) return false;
            return left.OrderBy(x => x == null ? string.Empty : x.ToString(), StringComparer.Ordinal)
                .SequenceEqual(right.OrderBy(x => x == null ? string.Empty : x.ToString(), StringComparer.Ordinal));
        }

        private static List<string> MergeChanges(IList<string> first, IList<string> second)
        {
            return first.Concat(second).Distinct(StringComparer.Ordinal).ToList();
        }

        private static IEnumerable<string> UnionKeys<T1, T2, T3>(
            IDictionary<string, T1> first,
            IDictionary<string, T2> second,
            IDictionary<string, T3> third)
        {
            return first.Keys.Concat(second.Keys).Concat(third.Keys)
                .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal);
        }

        private static T Get<T>(IDictionary<string, T> values, string key)
        {
            T value;
            return values.TryGetValue(key, out value) ? value : default(T);
        }

        private static bool FailIndex(ReconciliationPlan plan, DesignErrorCode code, string message)
        {
            plan.IsApplicable = false;
            plan.ErrorCode = code;
            plan.ErrorMessage = message;
            return false;
        }

        private static ReconciliationPlan Fail(ReconciliationPlan plan, DesignErrorCode code, string message)
        {
            plan.IsApplicable = false;
            plan.ErrorCode = code;
            plan.ErrorMessage = message;
            return plan;
        }
    }
}
