using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Autodesk.Revit.Attributes;

namespace FCUAutoDesign
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CmdPlaceFCUAndConnect : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                UIDocument uidoc = commandData.Application.ActiveUIDocument;
                if (uidoc == null) return Result.Cancelled;
                Document doc = uidoc.Document;
                // =========================================================================
                // 弹出 WPF 参数配置窗口，使设计师界面调整真正控制后续管道行为
                // =========================================================================
                FCUDesignWindow uiWindow = new FCUDesignWindow();
                List<FamilySymbol> fcuTypes = new FcuTypeCatalog().GetFCUSymbols(doc);
                if (fcuTypes.Count == 0)
                    throw new InvalidOperationException("请先载入指定的 FCU 机械设备族。");
                uiWindow.SetFcuTypes(fcuTypes.Select(x => new KeyValuePair<int, string>(
                    x.Id.IntegerValue, x.FamilyName + " : " + x.Name)).ToList());
                try
                {
                    IntPtr mainWindowHandle = commandData.Application.MainWindowHandle;
                    if (mainWindowHandle != IntPtr.Zero)
                    {
                        System.Windows.Interop.WindowInteropHelper helper = new System.Windows.Interop.WindowInteropHelper(uiWindow);
                        helper.Owner = mainWindowHandle;
                    }
                }
                catch
                {
                    // 在无窗体或自动化测试环境下继续运行
                }

                bool? dialogResult = uiWindow.ShowDialog();
                if (dialogResult != true || !uiWindow.IsConfirmed)
                {
                    return Result.Cancelled;
                }

                FcuDesignOptions options = new FcuDesignOptions
                {
                    SelectedFcuTypeId = uiWindow.SelectedFcuTypeId.Value,
                    DoorOffsetMm = uiWindow.DoorOffsetMm,
                    FcuElevationMm = uiWindow.FcuElevationMm,
                    ValveClearanceMm = uiWindow.ValveClearanceMm,
                    MinimumStraightLengthMm = uiWindow.MinimumStraightLengthMm,
                    FlipDropMm = uiWindow.FlipDropMm,
                    CoolingIndexWPerSquareMeter = uiWindow.CoolingIndexWPerSquareMeter,
                    EnableAutoSizing = uiWindow.EnableAutoSizing,
                    EnableBusinessRulePreview = uiWindow.EnableBusinessRulePreview,
                    EnableMultipleFcus = uiWindow.EnableMultipleFcus,
                    SelectedDoorIds = new Dictionary<int, ElementId>(),
                    EnableReturnPipe = uiWindow.EnableReturnPipe,
                    EnableCondensate = uiWindow.EnableCondensate,
                    BreakCurveAndTee = uiWindow.BreakCurveAndTee,
                    EnableAutomaticScopeDiscovery = uiWindow.EnableAutomaticScopeDiscovery,
                    AutomaticRoomNameKeywords = uiWindow.AutomaticRoomNameKeywords
                };

                // =========================================================================
                // 1. 引导用户拾取目标房间与走廊供水水平主管
                // =========================================================================
                int totalSteps = 2 + (options.EnableReturnPipe ? 1 : 0) + (options.EnableCondensate ? 1 : 0);
                AutomaticScopeDiscoveryService discoveryService = new AutomaticScopeDiscoveryService();
                List<Room> rooms = null;
                RoomDiscoveryResult automaticRoomDiscovery = null;
                if (options.EnableAutomaticScopeDiscovery)
                {
                    RoomDiscoveryResult discovered = discoveryService.DiscoverRooms(doc, doc.ActiveView,
                        options.AutomaticRoomNameKeywords);
                    bool? useDiscoveredRooms = discoveryService.ConfirmRooms(discovered);
                    if (!useDiscoveredRooms.HasValue) return Result.Cancelled;
                    if (useDiscoveredRooms.Value)
                    {
                        rooms = discovered.Rooms.ToList();
                        automaticRoomDiscovery = discovered;
                        foreach (KeyValuePair<int, ElementId> door in discovered.ResolvedDoorIds)
                            options.SelectedDoorIds[door.Key] = door.Value;
                    }
                }
                if (rooms == null)
                {
                    IList<Reference> roomRefs = uidoc.Selection.PickObjects(ObjectType.Element, new RoomFilter(),
                        $"【步骤 1/{totalSteps}】请选择同楼层房间，选完点击“完成”（多门房间随后指定目标门）");
                    rooms = roomRefs.Select(r => doc.GetElement(r) as Room)
                        .Where(r => r != null).GroupBy(r => r.Id.IntegerValue).Select(g => g.First()).ToList();
                }
                if (rooms.Count == 0) return Result.Cancelled;
                FcuBatchService.ValidateRooms(rooms);

                // 多门房间不能靠几何或门序号猜测 L 轴。让用户明确指定目标门，
                // 并把同一选择传给业务预览和后续放置流程。
                foreach (Room room in rooms)
                {
                    if (automaticRoomDiscovery != null
                        && options.SelectedDoorIds.ContainsKey(room.Id.IntegerValue)) continue;
                    List<FamilyInstance> doors = RoomRuleSnapshotReader.FindDoors(doc, room);
                    FamilyInstance automaticDoor = RoomRuleSnapshotReader.ResolveDoorForRoom(doc, room);
                    if (automaticDoor != null)
                    {
                        options.SelectedDoorIds[room.Id.IntegerValue] = automaticDoor.Id;
                    }
                    else if (doors.Count > 1)
                    {
                        Reference doorRef = uidoc.Selection.PickObject(
                            ObjectType.Element,
                            new RoomDoorFilter(doc, room.Id),
                            "房间 " + (room.Number ?? room.Id.IntegerValue.ToString())
                            + " 有多个门，请拾取作为 L 侧的目标门");
                        FamilyInstance selectedDoor = doc.GetElement(doorRef) as FamilyInstance;
                        if (selectedDoor == null || !RoomRuleSnapshotReader.BelongsToRoom(selectedDoor, room))
                            throw new InvalidOperationException("所选目标门不属于当前房间。");
                        options.SelectedDoorIds[room.Id.IntegerValue] = selectedDoor.Id;
                    }
                }

                PipeDiscoveryResult supplyDiscovery = null, returnDiscovery = null, drainDiscovery = null;
                bool useAutomaticPipes = false;
                List<AutomaticScopeZone> automaticZones = null;
                if (options.EnableAutomaticScopeDiscovery)
                {
                    supplyDiscovery = discoveryService.DiscoverPipe(doc, doc.ActiveView, rooms, options,
                        MEPSystemClassification.SupplyHydronic, "供水主管");
                    if (options.EnableReturnPipe)
                        returnDiscovery = discoveryService.DiscoverPipe(doc, doc.ActiveView, rooms, options,
                            MEPSystemClassification.ReturnHydronic, "回水主管");
                    if (options.EnableCondensate)
                        drainDiscovery = discoveryService.DiscoverPipe(doc, doc.ActiveView, rooms, options,
                            MEPSystemClassification.Sanitary, "冷凝水主管");
                    AutomaticZoneResult zoneResult = discoveryService.BuildZones(
                        rooms, supplyDiscovery, returnDiscovery, drainDiscovery, options);
                    bool? useDiscoveredZones = discoveryService.ConfirmZones(zoneResult);
                    if (!useDiscoveredZones.HasValue) return Result.Cancelled;
                    useAutomaticPipes = useDiscoveredZones.Value;
                    if (useAutomaticPipes)
                    {
                        automaticZones = zoneResult.Zones.ToList();
                        rooms = automaticZones.SelectMany(x => x.Rooms).ToList();
                    }
                }

                // 已登记房间先进入三方差异预览。只有纯计算字段变化可在明确确认后
                // 更新设计记录；任何模型几何、型号、数量或管线变化仍保持只读。
                ReconciliationPreviewOutcome reconciliation =
                    new DesignReconciliationPreviewService().ShowIfExisting(doc, rooms, options);
                if (reconciliation == ReconciliationPreviewOutcome.Applied)
                    return Result.Succeeded;
                if (reconciliation == ReconciliationPreviewOutcome.PreviewOnly)
                    return Result.Cancelled;

                if (options.EnableBusinessRulePreview)
                {
                    FamilySymbol selectedSymbol = doc.GetElement(
                        new ElementId(options.SelectedFcuTypeId)) as FamilySymbol;
                    string selectedTypeName = selectedSymbol == null
                        ? "未找到"
                        : selectedSymbol.FamilyName + " : " + selectedSymbol.Name;
                    if (!new FcuBusinessPreviewService().Confirm(doc, rooms, options, selectedTypeName))
                        return Result.Cancelled;
                }

                Pipe supplyMainPipe = automaticZones == null ? null : automaticZones[0].Supply;
                if (supplyMainPipe == null)
                {
                    Reference supplyRef = uidoc.Selection.PickObject(ObjectType.Element,
                        new PipeFilter(MEPSystemClassification.SupplyHydronic),
                        $"【步骤 2/{totalSteps}】请选择供水水平主管（仅接受 SupplyHydronic 系统分类）");
                    supplyMainPipe = doc.GetElement(supplyRef) as Pipe;
                }
                if (supplyMainPipe == null) return Result.Cancelled;

                // 2. 根据用户勾选决定是否提示拾取回水主管
                Pipe returnMainPipe = automaticZones == null ? null : automaticZones[0].Return;
                if (options.EnableReturnPipe)
                {
                    try
                    {
                        if (returnMainPipe == null)
                        {
                            Reference returnRef = uidoc.Selection.PickObject(ObjectType.Element, new PipeFilter(MEPSystemClassification.ReturnHydronic), $"【步骤 3/{totalSteps}】请选择回水水平主管（仅接受 ReturnHydronic 系统分类，按 ESC 可跳过）");
                            if (returnRef != null) returnMainPipe = doc.GetElement(returnRef) as Pipe;
                        }
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        // 用户主动按 ESC 跳过回水，保持 returnMainPipe 为 null
                    }
                }

                Pipe condensateMainPipe = automaticZones == null ? null : automaticZones[0].Condensate;
                if (options.EnableCondensate)
                {
                    if (condensateMainPipe == null)
                        condensateMainPipe = new CondensateMainPicker().Pick(uidoc, totalSteps);
                    if (condensateMainPipe == null) return Result.Cancelled;
                }

                var batchClock = System.Diagnostics.Stopwatch.StartNew();
                List<RoomExecutionResult> results = new List<RoomExecutionResult>();
                if (automaticZones == null)
                {
                    results.AddRange(new FcuBatchService().Execute(doc, rooms,
                        supplyMainPipe, returnMainPipe, condensateMainPipe, options));
                }
                else
                {
                    foreach (AutomaticScopeZone zone in automaticZones)
                        results.AddRange(new FcuBatchService().Execute(doc, zone.Rooms.ToList(),
                            zone.Supply, zone.Return, zone.Condensate, options));
                }
                batchClock.Stop();
                new BatchReportPresenter().Show(results, options, batchClock.Elapsed);
                // 部分房间失败不能以命令级 Failed 撤销已提交的其他房间。
                if (results.Any(r => r.Design != null)) return Result.Succeeded;
                message = "本批次没有房间完成布置，请查看各房间失败原因。";
                return Result.Failed;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.ToString();
                return Result.Failed;
            }
        }
    }
}
