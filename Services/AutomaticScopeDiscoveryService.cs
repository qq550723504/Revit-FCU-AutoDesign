using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Media.Media3D;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using FCUAutoDesign.Business.RoomSelection;

namespace FCUAutoDesign
{
    internal sealed class RoomDiscoveryResult
    {
        public IList<Room> Rooms { get; } = new List<Room>();
        public IList<string> Rejections { get; } = new List<string>();
        public IDictionary<int, ElementId> ResolvedDoorIds { get; } = new Dictionary<int, ElementId>();
    }

    internal sealed class PipeDiscoveryResult
    {
        public Pipe UniquePipe { get; set; }
        public IList<Pipe> EligiblePipes { get; } = new List<Pipe>();
        public string Label { get; set; }
    }

    internal sealed class AutomaticScopeDiscoveryService
    {
        private readonly FcuPlacementService placement = new FcuPlacementService();

        public RoomDiscoveryResult DiscoverRooms(Document doc, View view, IEnumerable<string> nameKeywords)
        {
            RoomDiscoveryResult result = new RoomDiscoveryResult();
            if (view == null || view.IsTemplate || view.GenLevel == null)
            {
                result.Rejections.Add("当前视图不是具有标高的平面视图，请改用手动选择。");
                return result;
            }
            DesignRecordRepository repository = new DesignRecordRepository(doc);
            foreach (Room room in new FilteredElementCollector(doc, view.Id)
                .OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().Cast<Room>()
                .OrderBy(x => x.Number, StringComparer.Ordinal).ThenBy(x => x.Id.IntegerValue))
            {
                string label = (room.Number ?? room.Id.IntegerValue.ToString()) + " " + (room.Name ?? string.Empty);
                DesignRecordReadResult existing = repository.LoadByRoomUniqueId(room.UniqueId);
                if (existing.Status == DesignOperationStatus.Succeeded)
                    result.Rejections.Add(label + "：已有 FCU 设计记录，自动新建批次已跳过；可手动选择进入重算。");
                else if (existing.Status != DesignOperationStatus.NotFound)
                    result.Rejections.Add(label + "：设计记录状态异常，未自动处理：" + existing.Message);
                else if (room.LevelId != view.GenLevel.Id) result.Rejections.Add(label + "：不在当前视图标高。");
                else if (room.Location == null || room.Area <= 0) result.Rejections.Add(label + "：未放置或未形成有效封闭面积。");
                else if (!RoomNameKeywordPolicy.Matches(room.Name, nameKeywords))
                    result.Rejections.Add(label + "：名称不匹配自动设计关键词。");
                else
                {
                    List<FamilyInstance> doors = RoomRuleSnapshotReader.FindDoors(doc, room);
                    List<FamilyInstance> validDoors = doors.Where(door =>
                        new RoomRuleSnapshotReader().Read(doc, room, door).IsValid).ToList();
                    if (validDoors.Count == 0)
                    {
                        string reason = doors.Count == 0
                            ? "没有关联门，无法确定门侧 L 轴。"
                            : new RoomRuleSnapshotReader().Read(doc, room, doors[0]).ErrorMessage;
                        result.Rejections.Add(label + "：" + reason);
                        continue;
                    }

                    FamilyInstance automaticDoor = RoomRuleSnapshotReader.ResolveDoorForRoom(doc, room);
                    FamilyInstance resolved = automaticDoor != null
                        && validDoors.Any(x => x.Id == automaticDoor.Id)
                        ? automaticDoor
                        : validDoors.Count == 1 ? validDoors[0] : null;
                    if (resolved != null) result.ResolvedDoorIds[room.Id.IntegerValue] = resolved.Id;
                    result.Rooms.Add(room);
                }
            }
            return result;
        }

        public PipeDiscoveryResult DiscoverPipe(Document doc, View view, IList<Room> rooms,
            FcuDesignOptions options, MEPSystemClassification classification, string label)
        {
            PipeDiscoveryResult result = new PipeDiscoveryResult { Label = label };
            if (view == null || view.IsTemplate || view.GenLevel == null
                || rooms == null || rooms.Count == 0) return result;
            ElementId levelId = rooms[0].LevelId;
            List<Point3D> approaches = rooms.SelectMany(room => placement.PlanPoints(doc, room, options))
                .Where(x => x != null).Select(Point).ToList();
            if (approaches.Count == 0) return result;

            List<Tuple<Pipe, Line>> candidates = new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(Pipe)).Cast<Pipe>()
                .Select(pipe => Tuple.Create(pipe, (pipe.Location as LocationCurve)?.Curve as Line))
                .Where(x => x.Item2 != null && x.Item2.IsBound
                    && Math.Abs(x.Item2.Direction.Z) < 1e-6
                    && x.Item1.ReferenceLevel != null && x.Item1.ReferenceLevel.Id == levelId
                    && Classification(doc, x.Item1) == classification).ToList();
            List<LinearMainCandidate> geometry = candidates.Select(x => new LinearMainCandidate
            {
                Id = x.Item1.UniqueId,
                Start = Point(x.Item2.GetEndPoint(0)),
                End = Point(x.Item2.GetEndPoint(1))
            }).ToList();
            FamilySymbol symbol = doc.GetElement(new ElementId(options.SelectedFcuTypeId)) as FamilySymbol;
            BoundingBoxXYZ familyBounds = symbol?.get_BoundingBox(null);
            double familyPlanExtent = familyBounds == null ? 0 : Math.Sqrt(
                Math.Pow(familyBounds.Max.X - familyBounds.Min.X, 2)
                + Math.Pow(familyBounds.Max.Y - familyBounds.Min.Y, 2));
            LinearMainCandidateResult selected = LinearMainCandidateSelector.Select(geometry, approaches,
                Math.Max(Math.Max(doc.Application.ShortCurveTolerance, RevitUnits.MM_TO_FEET), familyPlanExtent));
            foreach (string id in selected.EligibleCandidateIds)
                result.EligiblePipes.Add(candidates.Single(x => x.Item1.UniqueId == id).Item1);
            result.UniquePipe = string.IsNullOrEmpty(selected.UniqueCandidateId)
                ? null : candidates.Single(x => x.Item1.UniqueId == selected.UniqueCandidateId).Item1;
            return result;
        }

        public bool? ConfirmRooms(RoomDiscoveryResult discovery)
        {
            if (discovery.Rooms.Count == 0)
            {
                TaskDialog.Show("未发现可自动选择的房间",
                    (discovery.Rejections.Count == 0 ? "当前视图没有房间。" : string.Join(Environment.NewLine, discovery.Rejections))
                    + Environment.NewLine + "将改为手动拾取房间。");
                return false;
            }
            StringBuilder details = new StringBuilder();
            foreach (Room room in discovery.Rooms)
                details.AppendLine("使用：" + room.Number + " " + room.Name + "（ID " + room.Id.IntegerValue + "）");
            foreach (string rejection in discovery.Rejections) details.AppendLine("排除：" + rejection);
            TaskDialog dialog = new TaskDialog("自动发现房间")
            {
                MainInstruction = "发现 " + discovery.Rooms.Count + " 个当前视图同楼层有效房间",
                MainContent = "选择“是”使用这些房间；选择“否”改为手动拾取。",
                ExpandedContent = details.ToString(),
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No | TaskDialogCommonButtons.Cancel,
                DefaultButton = TaskDialogResult.No
            };
            TaskDialogResult choice = dialog.Show();
            return choice == TaskDialogResult.Cancel ? (bool?)null : choice == TaskDialogResult.Yes;
        }

        public bool? ConfirmPipes(IEnumerable<PipeDiscoveryResult> discoveries)
        {
            List<PipeDiscoveryResult> values = discoveries.ToList();
            StringBuilder content = new StringBuilder();
            foreach (PipeDiscoveryResult value in values)
                content.AppendLine(value.Label + "：" + (value.UniquePipe == null
                    ? value.EligiblePipes.Count == 0 ? "无覆盖全部房间的候选，将手动选择" : "存在多个候选，将手动选择"
                    : "元素 ID " + value.UniquePipe.Id.IntegerValue + "（唯一候选）"));
            if (!values.Any(x => x.UniquePipe != null))
            {
                TaskDialog.Show("未唯一确定主管", content + "将按原流程手动拾取主管。");
                return false;
            }
            TaskDialog dialog = new TaskDialog("自动发现主管")
            {
                MainInstruction = "已发现唯一主管候选，是否采用？",
                MainContent = content.ToString(),
                FooterText = "未唯一确定的回路仍会要求手动拾取。",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No | TaskDialogCommonButtons.Cancel,
                DefaultButton = TaskDialogResult.No
            };
            TaskDialogResult choice = dialog.Show();
            return choice == TaskDialogResult.Cancel ? (bool?)null : choice == TaskDialogResult.Yes;
        }

        private static MEPSystemClassification? Classification(Document doc, Pipe pipe)
        {
            ElementId id = pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId();
            return (doc.GetElement(id) as PipingSystemType)?.SystemClassification;
        }
        private static Point3D Point(XYZ p) { return new Point3D(p.X, p.Y, p.Z); }
    }
}
