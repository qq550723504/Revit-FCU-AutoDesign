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
                    FlipDropMm = uiWindow.FlipDropMm,
                    CoolingIndexWPerSquareMeter = uiWindow.CoolingIndexWPerSquareMeter,
                    EnableAutoSizing = uiWindow.EnableAutoSizing,
                    EnableBusinessRulePreview = uiWindow.EnableBusinessRulePreview,
                    SelectedDoorIds = new Dictionary<int, ElementId>(),
                    EnableReturnPipe = uiWindow.EnableReturnPipe,
                    EnableCondensate = uiWindow.EnableCondensate,
                    BreakCurveAndTee = uiWindow.BreakCurveAndTee
                };

                // =========================================================================
                // 1. 引导用户拾取目标房间与走廊供水水平主管
                // =========================================================================
                int totalSteps = 2 + (options.EnableReturnPipe ? 1 : 0) + (options.EnableCondensate ? 1 : 0);
                IList<Reference> roomRefs = uidoc.Selection.PickObjects(ObjectType.Element, new RoomFilter(),
                    $"【步骤 1/{totalSteps}】请选择同楼层房间，选完点击“完成”（多门房间随后指定目标门）");
                List<Room> rooms = roomRefs.Select(r => doc.GetElement(r) as Room)
                    .Where(r => r != null).GroupBy(r => r.Id.IntegerValue).Select(g => g.First()).ToList();
                if (rooms.Count == 0) return Result.Cancelled;
                FcuBatchService.ValidateRooms(rooms);

                // 多门房间不能靠几何或门序号猜测 L 轴。让用户明确指定目标门，
                // 并把同一选择传给业务预览和后续放置流程。
                foreach (Room room in rooms)
                {
                    List<FamilyInstance> doors = RoomRuleSnapshotReader.FindDoors(doc, room);
                    if (doors.Count == 1)
                    {
                        options.SelectedDoorIds[room.Id.IntegerValue] = doors[0].Id;
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

                Reference supplyRef = uidoc.Selection.PickObject(ObjectType.Element, new PipeFilter(MEPSystemClassification.SupplyHydronic), $"【步骤 2/{totalSteps}】请选择供水水平主管（仅接受 SupplyHydronic 系统分类）");
                Pipe supplyMainPipe = doc.GetElement(supplyRef) as Pipe;
                if (supplyMainPipe == null) return Result.Cancelled;

                // 2. 根据用户勾选决定是否提示拾取回水主管
                Pipe returnMainPipe = null;
                if (options.EnableReturnPipe)
                {
                    try
                    {
                        Reference returnRef = uidoc.Selection.PickObject(ObjectType.Element, new PipeFilter(MEPSystemClassification.ReturnHydronic), $"【步骤 3/{totalSteps}】请选择回水水平主管（仅接受 ReturnHydronic 系统分类，按 ESC 可跳过）");
                        if (returnRef != null)
                        {
                            returnMainPipe = doc.GetElement(returnRef) as Pipe;
                        }
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        // 用户主动按 ESC 跳过回水，保持 returnMainPipe 为 null
                    }
                }

                Pipe condensateMainPipe = null;
                if (options.EnableCondensate)
                {
                    condensateMainPipe = new CondensateMainPicker().Pick(uidoc, totalSteps);
                    if (condensateMainPipe == null) return Result.Cancelled;
                }

                List<RoomExecutionResult> results = new FcuBatchService().Execute(doc, rooms,
                    supplyMainPipe, returnMainPipe, condensateMainPipe, options);
                new BatchReportPresenter().Show(results, options);
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
