using System;
using System.Collections.Generic;
using System.Linq;

namespace FCUAutoDesign.Business.EquipmentSelection
{
    public sealed class EquipmentSelectionCalculator
    {
        private const double MaxSupportedLengthM = 40.0;
        private const double Epsilon = 1e-9;

        public EquipmentSelectionResult Calculate(
            EquipmentSelectionInput input,
            IEnumerable<EquipmentCatalogEntry> catalog,
            string preferredModelCode = null)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));

            EquipmentSelectionResult result = new EquipmentSelectionResult();
            List<EquipmentCatalogEntry> entries = catalog == null
                ? new List<EquipmentCatalogEntry>()
                : catalog.ToList();

            EquipmentSelectionErrorCode inputError = ValidateInput(input);
            if (inputError != EquipmentSelectionErrorCode.None)
                return Fail(result, inputError, InputErrorMessage(inputError));

            result.AreaSquareMeters = input.LengthM * input.WidthM;
            result.DesignLoadKw = input.CoolingIndexWPerSquareMeter * result.AreaSquareMeters / 1000.0;

            if (result.DesignLoadKw <= Epsilon)
                return Fail(result, EquipmentSelectionErrorCode.ZeroLoadRequiresConfirmation,
                    "冷指标为零，输入合法但零负荷是否布置设备尚未确认。");

            if (input.LengthM > MaxSupportedLengthM + Epsilon)
                return Fail(result, EquipmentSelectionErrorCode.LengthOutsideSupportedRange,
                    "L 超出 40m 的已确认台数规则范围，不自动外推台数。");

            result.UnitCount = (int)Math.Ceiling(input.LengthM / 5.0);
            result.UnitDesignLoadKw = result.DesignLoadKw / result.UnitCount;

            if (entries.Count == 0)
                return Fail(result, EquipmentSelectionErrorCode.EmptyCatalog, "设备目录为空，无法选择型号。");

            List<EquipmentCatalogEntry> seriesEntries = entries
                .Where(x => string.Equals(x.SeriesId, input.SeriesId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (seriesEntries.Count == 0)
                return Fail(result, EquipmentSelectionErrorCode.EmptyCatalog,
                    "当前系列在设备目录中没有条目。");

            EquipmentCatalogEntry invalid = seriesEntries.FirstOrDefault(IsInvalidCatalogEntry);
            if (invalid != null)
                return Fail(result, EquipmentSelectionErrorCode.InvalidCatalogEntry,
                    "设备目录包含缺少型号或非正额定制冷量的条目。");

            double capacity = result.UnitDesignLoadKw;
            result.CapacityCandidates = seriesEntries
                .Where(x => x.RatedCoolingCapacityKw + Epsilon >= capacity)
                .OrderBy(x => x.RatedCoolingCapacityKw)
                .ThenBy(x => x.ModelCode, StringComparer.Ordinal)
                .ToList();
            if (result.CapacityCandidates.Count == 0)
                return Fail(result, EquipmentSelectionErrorCode.CapacityNotFound,
                    "目录中没有额定制冷量足以覆盖单台设计负荷的型号。");

            double minimumCapacity = result.CapacityCandidates[0].RatedCoolingCapacityKw;
            List<EquipmentCatalogEntry> tied = result.CapacityCandidates
                .Where(x => Math.Abs(x.RatedCoolingCapacityKw - minimumCapacity) <= Epsilon)
                .ToList();
            EquipmentCatalogEntry selected = SelectTie(tied, preferredModelCode);
            if (selected == null)
                return Fail(result, EquipmentSelectionErrorCode.AmbiguousCapacityMatch,
                    "最小满足容量对应多个型号，需提供明确型号或选择优先级。");

            result.SelectedEquipment = selected;
            for (int i = 1; i <= result.UnitCount; i++)
            {
                result.PlacementPoints.Add(new EquipmentPlacementPoint
                {
                    Sequence = i,
                    XAlongLengthM = input.LengthM * i / (result.UnitCount + 1),
                    YFromReferenceEdgeM = input.PlacementOffsetM,
                    AbsoluteElevationM = input.BaseElevationM + input.InstallationHeightM
                });
            }

            result.Success = true;
            result.ErrorCode = EquipmentSelectionErrorCode.None;
            result.Explanation = string.Format(
                "A={0:F3}m²，Q={1:F3}kW，FP={2}，单台={3:F3}kW；选择 {4}（额定 {5:F3}kW）。",
                result.AreaSquareMeters, result.DesignLoadKw, result.UnitCount,
                result.UnitDesignLoadKw, selected.ModelCode, selected.RatedCoolingCapacityKw);
            return result;
        }

        private static EquipmentSelectionErrorCode ValidateInput(EquipmentSelectionInput input)
        {
            if (string.IsNullOrWhiteSpace(input.SeriesId)) return EquipmentSelectionErrorCode.MissingSeries;
            if (string.IsNullOrWhiteSpace(input.CatalogVersion)) return EquipmentSelectionErrorCode.MissingCatalogVersion;
            if (double.IsNaN(input.LengthM) || double.IsInfinity(input.LengthM) || input.LengthM <= 0)
                return EquipmentSelectionErrorCode.InvalidLength;
            if (double.IsNaN(input.WidthM) || double.IsInfinity(input.WidthM) || input.WidthM <= 0)
                return EquipmentSelectionErrorCode.InvalidWidth;
            if (double.IsNaN(input.CoolingIndexWPerSquareMeter)
                || double.IsInfinity(input.CoolingIndexWPerSquareMeter)
                || input.CoolingIndexWPerSquareMeter < 0)
                return EquipmentSelectionErrorCode.InvalidCoolingIndex;
            if (double.IsNaN(input.PlacementOffsetM) || double.IsInfinity(input.PlacementOffsetM)
                || input.PlacementOffsetM < 0 || input.PlacementOffsetM >= input.WidthM)
                return EquipmentSelectionErrorCode.PlacementOffsetOutsideRoom;
            return EquipmentSelectionErrorCode.None;
        }

        private static bool IsInvalidCatalogEntry(EquipmentCatalogEntry entry)
        {
            return entry == null || string.IsNullOrWhiteSpace(entry.ModelCode)
                || entry.RatedCoolingCapacityKw <= 0
                || double.IsNaN(entry.RatedCoolingCapacityKw)
                || double.IsInfinity(entry.RatedCoolingCapacityKw);
        }

        private static EquipmentCatalogEntry SelectTie(
            List<EquipmentCatalogEntry> tied, string preferredModelCode)
        {
            if (!string.IsNullOrWhiteSpace(preferredModelCode))
                return tied.FirstOrDefault(x => string.Equals(
                    x.ModelCode, preferredModelCode, StringComparison.OrdinalIgnoreCase));
            if (tied.Count == 1) return tied[0];
            int priorityCount = tied.Count(x => x.SelectionPriority.HasValue);
            if (priorityCount == tied.Count)
            {
                int bestPriority = tied.Min(x => x.SelectionPriority.Value);
                List<EquipmentCatalogEntry> prioritized = tied
                    .Where(x => x.SelectionPriority.Value == bestPriority).ToList();
                if (prioritized.Count == 1) return prioritized[0];
            }
            return null;
        }

        private static EquipmentSelectionResult Fail(
            EquipmentSelectionResult result,
            EquipmentSelectionErrorCode code,
            string message)
        {
            result.Success = false;
            result.ErrorCode = code;
            result.ErrorMessage = message;
            return result;
        }

        private static string InputErrorMessage(EquipmentSelectionErrorCode code)
        {
            switch (code)
            {
                case EquipmentSelectionErrorCode.MissingSeries: return "必须指定设备系列。";
                case EquipmentSelectionErrorCode.MissingCatalogVersion: return "必须指定设备目录版本。";
                case EquipmentSelectionErrorCode.InvalidLength: return "L 必须是正数。";
                case EquipmentSelectionErrorCode.InvalidWidth: return "H 必须是正数。";
                case EquipmentSelectionErrorCode.InvalidCoolingIndex: return "冷指标必须是大于或等于零的数值。";
                case EquipmentSelectionErrorCode.PlacementOffsetOutsideRoom: return "局部布置点距 L 边的距离必须落在房间宽度内。";
                default: return "设备规则输入无效。";
            }
        }
    }
}
