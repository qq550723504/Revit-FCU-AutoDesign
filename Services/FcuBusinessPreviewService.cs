using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

        public bool Confirm(Document doc, IList<Room> rooms, FcuDesignOptions options, string selectedTypeName,
            RoomDiscoveryResult roomDiscovery = null, AutomaticZoneResult zoneResult = null,
            bool includeBusinessCalculation = true)
        {
            List<string> lines = new List<string>();
            if (includeBusinessCalculation) foreach (Room room in rooms)
            {
                FamilyInstance selectedDoor = null;
                ElementId selectedDoorId;
                if (options.SelectedDoorIds != null
                    && options.SelectedDoorIds.TryGetValue(room.Id.IntegerValue, out selectedDoorId))
                {
                    selectedDoor = doc.GetElement(selectedDoorId) as FamilyInstance;
                }
                RoomRuleSnapshot snapshot = roomReader.Read(doc, room, selectedDoor);
                if (!snapshot.IsValid)
                {
                    TaskDialog.Show("FCU 业务预览无法计算",
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
                        PlacementOffsetM = options.DoorOffsetMm / 1000.0
                    }, PreviewCatalog);
                if (!result.Success)
                {
                    TaskDialog.Show("FCU 业务预览无法计算",
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

            StringBuilder main = new StringBuilder();
            main.AppendLine("模式：" + (options.OperationMode == FcuOperationMode.Create ? "首次生成" : "修改重算"));
            main.AppendLine("目标房间：" + rooms.Count + " 个；Revit 族类型：" + selectedTypeName);
            main.AppendLine(string.Format("冷指标 {0:0.##} W/m²；距门侧墙 {1:0.##} mm；安装高度 {2:0.##} mm；{3}",
                options.CoolingIndexWPerSquareMeter, options.DoorOffsetMm, options.FcuElevationMm,
                options.EnableMultipleFcus ? "多台均布" : "每间一台"));
            main.AppendLine("连接：供水" + (options.EnableReturnPipe ? "、回水" : string.Empty)
                + (options.EnableCondensate ? "、冷凝水" : string.Empty)
                + (options.BreakCurveAndTee ? "；主管打断插三通" : "；不打断主管"));
            if (zoneResult != null)
                main.AppendLine("自动主管区域：" + zoneResult.Zones.Count + " 个；未覆盖或不唯一房间："
                    + zoneResult.Rejections.Count + " 个。");
            if (includeBusinessCalculation)
                main.AppendLine("业务计算：已生成每房间负荷、台数和建议型号，详见展开内容。");

            StringBuilder details = new StringBuilder();
            details.AppendLine("【使用房间】");
            foreach (Room room in rooms)
                details.AppendLine(room.Number + " " + room.Name + "（ID " + room.Id.IntegerValue + "）");
            if (zoneResult != null)
            {
                details.AppendLine("【自动主管区域】");
                for (int i = 0; i < zoneResult.Zones.Count; i++)
                {
                    AutomaticScopeZone zone = zoneResult.Zones[i];
                    details.AppendLine("区域 " + (i + 1) + "：" + zone.Rooms.Count + " 个房间；供水 ID "
                        + zone.Supply.Id.IntegerValue
                        + (zone.Return == null ? string.Empty : "，回水 ID " + zone.Return.Id.IntegerValue)
                        + (zone.Condensate == null ? string.Empty : "，冷凝水 ID " + zone.Condensate.Id.IntegerValue));
                }
            }
            if (lines.Count > 0)
            {
                details.AppendLine("【业务计算】");
                foreach (string line in lines) details.AppendLine(line);
            }
            List<string> exclusions = new List<string>();
            if (roomDiscovery != null) exclusions.AddRange(roomDiscovery.Rejections);
            if (zoneResult != null) exclusions.AddRange(zoneResult.Rejections);
            if (exclusions.Count > 0)
            {
                details.AppendLine("【未自动处理】");
                foreach (string rejection in exclusions.Take(100)) details.AppendLine(rejection);
                if (exclusions.Count > 100) details.AppendLine("其余 " + (exclusions.Count - 100) + " 项未展开。");
            }

            TaskDialog dialog = new TaskDialog("FCU 综合执行预览");
            dialog.MainInstruction = "确认一次后开始放置与接管";
            dialog.MainContent = main.ToString();
            dialog.ExpandedContent = details.ToString();
            dialog.FooterText = "确认后才会进入现有 Revit 事务；任一房间接管失败时该房间整体回滚。目录型号仅为建议，不会自动替换所选族类型或启用正式选径。";
            dialog.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;
            dialog.DefaultButton = TaskDialogResult.No;
            return dialog.Show() == TaskDialogResult.Yes;
        }
    }
}
