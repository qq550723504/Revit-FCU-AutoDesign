using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using FCUAutoDesign.Business.EquipmentSelection;

namespace FCUAutoDesign
{
    internal sealed class FcuBusinessPreviewService
    {
        private readonly RoomRuleSnapshotReader roomReader = new RoomRuleSnapshotReader();
        private readonly EquipmentSelectionCalculator calculator = new EquipmentSelectionCalculator();

        // These values are transcribed from the implementation package's PRD table.
        // They are used only for the FCU-201 preview until the customer catalog mapping is confirmed.
        private static readonly EquipmentCatalogEntry[] PreviewCatalog =
        {
            new EquipmentCatalogEntry { SeriesId = "FP", ModelCode = "FP-136", RatedCoolingCapacityKw = 6.95 },
            new EquipmentCatalogEntry { SeriesId = "FP", ModelCode = "FP-102", RatedCoolingCapacityKw = 5.30 },
            new EquipmentCatalogEntry { SeriesId = "FP", ModelCode = "FP-85", RatedCoolingCapacityKw = 4.23 },
            new EquipmentCatalogEntry { SeriesId = "FP", ModelCode = "FP-68", RatedCoolingCapacityKw = 3.50 },
            new EquipmentCatalogEntry { SeriesId = "FP", ModelCode = "FP-51", RatedCoolingCapacityKw = 2.70 },
            new EquipmentCatalogEntry { SeriesId = "FP", ModelCode = "FP-34", RatedCoolingCapacityKw = 1.80 }
        };

        public bool Confirm(Document doc, IList<Room> rooms, FcuDesignOptions options, string selectedTypeName)
        {
            List<string> lines = new List<string>();
            foreach (Room room in rooms)
            {
                RoomRuleSnapshot snapshot = roomReader.Read(room);
                if (!snapshot.IsValid)
                {
                    TaskDialog.Show("FCU-201 预览无法计算",
                        "房间 " + (room.Number ?? room.Id.IntegerValue.ToString()) + "：" + snapshot.ErrorMessage);
                    return false;
                }

                EquipmentSelectionResult result = calculator.Calculate(
                    new EquipmentSelectionInput
                    {
                        RoomUniqueId = room.UniqueId,
                        SeriesId = "FP",
                        CatalogVersion = "prd-transcribed-v1",
                        LengthM = snapshot.LengthM,
                        WidthM = snapshot.WidthM,
                        CoolingIndexWPerSquareMeter = options.CoolingIndexWPerSquareMeter,
                        BaseElevationM = snapshot.BaseElevationM,
                        InstallationHeightM = options.FcuElevationMm / 1000.0,
                        PlacementOffsetM = 0.5
                    }, PreviewCatalog);
                if (!result.Success)
                {
                    TaskDialog.Show("FCU-201 预览无法计算",
                        "房间 " + (room.Number ?? room.Id.IntegerValue.ToString()) + "："
                        + result.ErrorMessage);
                    return false;
                }

                lines.Add(string.Format(
                    "房间 {0}：L={1:F2}m，H={2:F2}m，Q={3:F2}kW，台数={4}，型号={5}，单台={6:F2}kW；点位 {7}",
                    room.Number ?? room.Id.IntegerValue.ToString(), snapshot.LengthM, snapshot.WidthM,
                    result.DesignLoadKw, result.UnitCount, result.SelectedEquipment.ModelCode,
                    result.UnitDesignLoadKw, string.Join("、", result.PlacementPoints.Select(x =>
                        string.Format("({0:F2},{1:F2},{2:F2})m", x.XAlongLengthM,
                            x.YFromReferenceEdgeM, x.AbsoluteElevationM)))));
            }

            TaskDialog dialog = new TaskDialog("FCU-201 业务规则预览");
            dialog.MainInstruction = "请确认 FCU 业务规则计算方案";
            dialog.MainContent = string.Join(Environment.NewLine, lines)
                + Environment.NewLine + Environment.NewLine
                + "当前选择的 Revit 族类型：" + selectedTypeName + Environment.NewLine
                + "确认后继续现有 PoC 接管事务；本轮预览不会自动替换族类型、修改管径或保存设计记录。";
            dialog.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;
            dialog.DefaultButton = TaskDialogResult.No;
            return dialog.Show() == TaskDialogResult.Yes;
        }
    }
}
