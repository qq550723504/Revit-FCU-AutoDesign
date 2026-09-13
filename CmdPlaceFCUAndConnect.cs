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
                    EnableAutoSizing = uiWindow.EnableAutoSizing,
                    EnableReturnPipe = uiWindow.EnableReturnPipe,
                    EnableCondensate = uiWindow.EnableCondensate,
                    BreakCurveAndTee = uiWindow.BreakCurveAndTee
                };

                // =========================================================================
                // 1. 引导用户拾取目标房间与走廊供水水平主管
                // =========================================================================
                int totalSteps = 2 + (options.EnableReturnPipe ? 1 : 0) + (options.EnableCondensate ? 1 : 0);
                Reference roomRef = uidoc.Selection.PickObject(ObjectType.Element, new RoomFilter(), $"【步骤 1/{totalSteps}】请选择需要布置风机盘管的房间 (Room)");
                Room room = doc.GetElement(roomRef) as Room;
                if (room == null) return Result.Cancelled;

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

                FcuDesignResult result = new FcuDesignService().Execute(doc, room, supplyMainPipe,
                    returnMainPipe, condensateMainPipe, options);
                new ExecutionReportPresenter().ShowHonestReport(result.Outcome, result.RoomAreaSqm,
                    result.CoolingLoadKw, result.ActualDn, options.EnableAutoSizing,
                    options.EnableReturnPipe, returnMainPipe != null);

                return Result.Succeeded;
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
