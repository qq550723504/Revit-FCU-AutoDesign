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
        public ISet<int> CoveredRoomIds { get; } = new HashSet<int>();
        public int TotalRoomCount { get; set; }
        public string Label { get; set; }
        public IList<PipeRunCandidate> Runs { get; } = new List<PipeRunCandidate>();
    }

    internal sealed class PipeRunCandidate
    {
        public Pipe Pipe { get; set; }
        public ISet<int> CoveredRoomIds { get; } = new HashSet<int>();
    }

    internal sealed class AutomaticScopeZone
    {
        public Pipe Supply { get; set; }
        public Pipe Return { get; set; }
        public Pipe Condensate { get; set; }
        public IList<Room> Rooms { get; } = new List<Room>();
    }

    internal sealed class AutomaticZoneResult
    {
        public IList<AutomaticScopeZone> Zones { get; } = new List<AutomaticScopeZone>();
        public IList<string> Rejections { get; } = new List<string>();
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
            result.TotalRoomCount = rooms.Count;
            Dictionary<int, IList<Point3D>> roomApproaches = rooms.ToDictionary(
                room => room.Id.IntegerValue,
                room => (IList<Point3D>)placement.PlanPoints(doc, room, options)
                    .Where(x => x != null).Select(Point).ToList());
            if (roomApproaches.Values.All(x => x.Count == 0)) return result;

            List<Tuple<Pipe, Line>> candidates = new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(Pipe)).Cast<Pipe>()
                .Select(pipe => Tuple.Create(pipe, (pipe.Location as LocationCurve)?.Curve as Line))
                .Where(x => x.Item2 != null && x.Item2.IsBound
                    && Math.Abs(x.Item2.Direction.Z) < 1e-6
                    && x.Item1.ReferenceLevel != null && x.Item1.ReferenceLevel.Id == levelId
                    && Classification(doc, x.Item1) == classification).ToList();
            FamilySymbol symbol = doc.GetElement(new ElementId(options.SelectedFcuTypeId)) as FamilySymbol;
            BoundingBoxXYZ familyBounds = symbol?.get_BoundingBox(null);
            double familyPlanExtent = familyBounds == null ? 0 : Math.Sqrt(
                Math.Pow(familyBounds.Max.X - familyBounds.Min.X, 2)
                + Math.Pow(familyBounds.Max.Y - familyBounds.Min.Y, 2));
            double endpointTolerance = Math.Max(
                Math.Max(doc.Application.ShortCurveTolerance, RevitUnits.MM_TO_FEET), familyPlanExtent);
            Dictionary<string, Tuple<Pipe, IList<LinearMainCandidate>>> runs =
                new Dictionary<string, Tuple<Pipe, IList<LinearMainCandidate>>>(StringComparer.Ordinal);
            foreach (Pipe candidate in candidates.Select(x => x.Item1))
            {
                IList<Pipe> runPipes = new MainPipeRun(candidate).ExistingSegments(doc);
                string key = string.Join("|", runPipes.Select(x => x.UniqueId).OrderBy(x => x, StringComparer.Ordinal));
                if (runs.ContainsKey(key)) continue;
                Pipe representative = runPipes.OrderByDescending(x => ((x.Location as LocationCurve)?.Curve?.Length ?? 0))
                    .ThenBy(x => x.UniqueId, StringComparer.Ordinal).First();
                IList<LinearMainCandidate> segments = runPipes.Select(pipe =>
                {
                    Line line = (pipe.Location as LocationCurve)?.Curve as Line;
                    return new LinearMainCandidate
                    {
                        Id = pipe.UniqueId,
                        Start = Point(line.GetEndPoint(0)),
                        End = Point(line.GetEndPoint(1))
                    };
                }).ToList();
                runs.Add(key, Tuple.Create(representative, segments));
            }
            Dictionary<int, double> roomReach = rooms.ToDictionary(room => room.Id.IntegerValue,
                room => AutomaticPipeReach(room, view, familyPlanExtent));
            Dictionary<int, List<Tuple<Pipe, double>>> eligibleByRoom = roomApproaches.ToDictionary(
                room => room.Key, room => runs.Values.Select(run => Tuple.Create(run.Item1,
                    LinearMainCandidateSelector.MaximumPerpendicularDistance(
                        run.Item2, room.Value, endpointTolerance)))
                    .Where(x => x.Item2.HasValue && x.Item2.Value <= roomReach[room.Key])
                    .Select(x => Tuple.Create(x.Item1, x.Item2.Value)).ToList());
            double equalDistanceTolerance = Math.Max(doc.Application.ShortCurveTolerance, RevitUnits.MM_TO_FEET);
            List<Tuple<Pipe, IList<int>>> scored = runs.Values.Select(run => Tuple.Create(run.Item1,
                (IList<int>)eligibleByRoom.Where(room => room.Value.Count > 0
                    && room.Value.Where(x => x.Item2 <= room.Value.Min(y => y.Item2) + equalDistanceTolerance)
                        .Any(x => x.Item1.Id == run.Item1.Id))
                    .Select(room => room.Key).ToList())).ToList();
            foreach (Tuple<Pipe, IList<int>> run in scored.Where(x => x.Item2.Count > 0))
            {
                PipeRunCandidate candidate = new PipeRunCandidate { Pipe = run.Item1 };
                foreach (int roomId in run.Item2) candidate.CoveredRoomIds.Add(roomId);
                result.Runs.Add(candidate);
            }
            int bestCoverage = scored.Count == 0 ? 0 : scored.Max(x => x.Item2.Count);
            foreach (Tuple<Pipe, IList<int>> run in scored.Where(x => x.Item2.Count == bestCoverage && bestCoverage > 0))
                result.EligiblePipes.Add(run.Item1);
            if (result.EligiblePipes.Count == 1)
            {
                result.UniquePipe = result.EligiblePipes[0];
                foreach (int roomId in scored.Single(x => x.Item1.Id == result.UniquePipe.Id).Item2)
                    result.CoveredRoomIds.Add(roomId);
            }
            return result;
        }

        public AutomaticZoneResult BuildZones(IList<Room> rooms, PipeDiscoveryResult supply,
            PipeDiscoveryResult returnResult, PipeDiscoveryResult condensate, FcuDesignOptions options)
        {
            AutomaticZoneResult result = new AutomaticZoneResult();
            Dictionary<string, AutomaticScopeZone> zones = new Dictionary<string, AutomaticScopeZone>();
            foreach (Room room in rooms)
            {
                List<PipeRunCandidate> supplyRuns = Covering(supply, room);
                List<PipeRunCandidate> returnRuns = options.EnableReturnPipe ? Covering(returnResult, room) : new List<PipeRunCandidate>();
                List<PipeRunCandidate> drainRuns = options.EnableCondensate ? Covering(condensate, room) : new List<PipeRunCandidate>();
                bool valid = supplyRuns.Count == 1
                    && (!options.EnableReturnPipe || returnRuns.Count == 1)
                    && (!options.EnableCondensate || drainRuns.Count == 1);
                if (!valid)
                {
                    result.Rejections.Add(room.Number + " " + room.Name + "："
                        + CoverageReason("供水", supplyRuns.Count)
                        + (options.EnableReturnPipe ? "；" + CoverageReason("回水", returnRuns.Count) : string.Empty)
                        + (options.EnableCondensate ? "；" + CoverageReason("冷凝水", drainRuns.Count) : string.Empty));
                    continue;
                }
                Pipe supplyPipe = supplyRuns[0].Pipe;
                Pipe returnPipe = options.EnableReturnPipe ? returnRuns[0].Pipe : null;
                Pipe drainPipe = options.EnableCondensate ? drainRuns[0].Pipe : null;
                string key = supplyPipe.UniqueId + "|" + (returnPipe?.UniqueId ?? string.Empty)
                    + "|" + (drainPipe?.UniqueId ?? string.Empty);
                AutomaticScopeZone zone;
                if (!zones.TryGetValue(key, out zone))
                {
                    zone = new AutomaticScopeZone { Supply = supplyPipe, Return = returnPipe, Condensate = drainPipe };
                    zones.Add(key, zone);
                }
                zone.Rooms.Add(room);
            }
            foreach (AutomaticScopeZone zone in zones.Values) result.Zones.Add(zone);
            return result;
        }

        public bool? ConfirmZones(AutomaticZoneResult result)
        {
            if (result.Zones.Count == 0)
            {
                TaskDialog.Show("未形成自动主管区域", string.Join(Environment.NewLine, result.Rejections)
                    + Environment.NewLine + "将改为手动选择。");
                return false;
            }
            StringBuilder content = new StringBuilder();
            for (int i = 0; i < result.Zones.Count; i++)
            {
                AutomaticScopeZone zone = result.Zones[i];
                content.AppendLine("区域 " + (i + 1) + "：" + zone.Rooms.Count + " 个房间；供水 ID "
                    + zone.Supply.Id.IntegerValue
                    + (zone.Return == null ? string.Empty : "，回水 ID " + zone.Return.Id.IntegerValue)
                    + (zone.Condensate == null ? string.Empty : "，冷凝水 ID " + zone.Condensate.Id.IntegerValue));
            }
            TaskDialog dialog = new TaskDialog("自动主管区域确认")
            {
                MainInstruction = "发现 " + result.Zones.Count + " 个独立横向主管区域",
                MainContent = content.ToString(),
                ExpandedContent = string.Join(Environment.NewLine, result.Rejections.Select(x => "排除：" + x)),
                FooterText = "各区域独立执行，不会生成连接不同区域的主管。",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No | TaskDialogCommonButtons.Cancel,
                DefaultButton = TaskDialogResult.No
            };
            TaskDialogResult choice = dialog.Show();
            return choice == TaskDialogResult.Cancel ? (bool?)null : choice == TaskDialogResult.Yes;
        }

        private static List<PipeRunCandidate> Covering(PipeDiscoveryResult result, Room room)
        {
            return result == null ? new List<PipeRunCandidate>()
                : result.Runs.Where(x => x.CoveredRoomIds.Contains(room.Id.IntegerValue)).ToList();
        }

        private static string CoverageReason(string label, int count)
        {
            return count == 0 ? label + "主管未覆盖" : count == 1 ? label + "主管已确定" : label + "主管归属不唯一";
        }

        private static double AutomaticPipeReach(Room room, View view, double familyPlanExtent)
        {
            BoundingBoxXYZ bounds = room.get_BoundingBox(view) ?? room.get_BoundingBox(null);
            if (bounds == null) return familyPlanExtent;
            double width = bounds.Max.X - bounds.Min.X;
            double depth = bounds.Max.Y - bounds.Min.Y;
            return Math.Sqrt(width * width + depth * depth) + familyPlanExtent;
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

        public bool? ConfirmPipes(IEnumerable<PipeDiscoveryResult> discoveries, IEnumerable<Room> rooms)
        {
            List<PipeDiscoveryResult> values = discoveries.ToList();
            StringBuilder content = new StringBuilder();
            foreach (PipeDiscoveryResult value in values)
                content.AppendLine(value.Label + "：" + (value.UniquePipe == null
                    ? value.EligiblePipes.Count == 0 ? "没有可用候选，将手动选择" : "最佳覆盖数并列，将手动选择"
                    : "元素 ID " + value.UniquePipe.Id.IntegerValue + "；覆盖 "
                        + value.CoveredRoomIds.Count + "/" + value.TotalRoomCount + " 个房间"));
            if (!values.Any(x => x.UniquePipe != null))
            {
                TaskDialog.Show("未唯一确定主管", content + "将按原流程手动拾取主管。");
                return false;
            }
            TaskDialog dialog = new TaskDialog("自动发现主管")
            {
                MainInstruction = "已发现唯一主管候选，是否采用？",
                MainContent = content.ToString(),
                ExpandedContent = ExcludedRoomDetails(values, rooms),
                FooterText = "采用自动主管后，只处理所有已确定主管共同覆盖的房间；其他房间不自动生成。",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No | TaskDialogCommonButtons.Cancel,
                DefaultButton = TaskDialogResult.No
            };
            TaskDialogResult choice = dialog.Show();
            return choice == TaskDialogResult.Cancel ? (bool?)null : choice == TaskDialogResult.Yes;
        }

        private static string ExcludedRoomDetails(IList<PipeDiscoveryResult> discoveries, IEnumerable<Room> rooms)
        {
            List<PipeDiscoveryResult> resolved = discoveries.Where(x => x.UniquePipe != null).ToList();
            if (resolved.Count == 0) return string.Empty;
            return string.Join(Environment.NewLine, rooms.Where(room =>
                resolved.Any(x => !x.CoveredRoomIds.Contains(room.Id.IntegerValue)))
                .Select(room => "排除：" + room.Number + " " + room.Name + "；现有横向主管未覆盖。"));
        }

        private static MEPSystemClassification? Classification(Document doc, Pipe pipe)
        {
            ElementId id = pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId();
            return (doc.GetElement(id) as PipingSystemType)?.SystemClassification;
        }
        private static Point3D Point(XYZ p) { return new Point3D(p.X, p.Y, p.Z); }
    }
}
