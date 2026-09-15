using System;
using System.Collections.Generic;
using System.Linq;

namespace FCUAutoDesign.Business.Hydraulic
{
    public enum HydraulicMeasureKind
    {
        DesignCoolingLoad,
        RatedCoolingCapacity,
        Flow
    }

    public enum HydraulicUnit
    {
        Kilowatt,
        LitersPerSecond,
        CubicMetersPerHour
    }

    public enum HydraulicTableErrorCode
    {
        None,
        MissingMetadata,
        InvalidVersion,
        InvalidRow,
        InvalidBounds,
        Overlap,
        Gap,
        DuplicateDiameter,
        InvalidQuantity,
        NoMatchingBand,
        AmbiguousMatch
    }

    public sealed class HydraulicTableBand
    {
        public double LowerBound { get; set; }
        public bool LowerInclusive { get; set; }
        public double UpperBound { get; set; }
        public bool UpperInclusive { get; set; }
        public string NominalDiameter { get; set; }
    }

    public sealed class HydraulicTableDefinition
    {
        public string TableId { get; set; }
        public string Version { get; set; }
        public HydraulicMeasureKind InputQuantity { get; set; }
        public HydraulicUnit Unit { get; set; }
        public string LoadBasis { get; set; }
        public IList<HydraulicTableBand> Bands { get; private set; }

        public HydraulicTableDefinition()
        {
            Bands = new List<HydraulicTableBand>();
        }
    }

    public sealed class HydraulicTableValidationResult
    {
        public bool IsValid { get; internal set; }
        public HydraulicTableErrorCode ErrorCode { get; internal set; }
        public string ErrorMessage { get; internal set; }
        public IList<int> InvalidRows { get; private set; }

        public HydraulicTableValidationResult()
        {
            InvalidRows = new List<int>();
        }
    }

    public sealed class HydraulicLookupResult
    {
        public bool Success { get; internal set; }
        public HydraulicTableErrorCode ErrorCode { get; internal set; }
        public string ErrorMessage { get; internal set; }
        public HydraulicTableBand Band { get; internal set; }
    }

    public sealed class HydraulicTableValidator
    {
        private const double Epsilon = 1e-9;

        public HydraulicTableValidationResult Validate(HydraulicTableDefinition table)
        {
            HydraulicTableValidationResult result = new HydraulicTableValidationResult();
            if (table == null)
                return Fail(result, HydraulicTableErrorCode.MissingMetadata, "水力表不能为空。");
            if (string.IsNullOrWhiteSpace(table.TableId)
                || string.IsNullOrWhiteSpace(table.Version)
                || string.IsNullOrWhiteSpace(table.LoadBasis))
                return Fail(result, HydraulicTableErrorCode.MissingMetadata,
                    "水力表必须包含表 ID、版本和负荷依据。");
            if (table.Bands == null || table.Bands.Count == 0)
                return Fail(result, HydraulicTableErrorCode.InvalidRow, "水力表至少需要一行区间。");

            for (int i = 0; i < table.Bands.Count; i++)
            {
                HydraulicTableBand band = table.Bands[i];
                if (band == null || string.IsNullOrWhiteSpace(band.NominalDiameter))
                    return FailRow(result, HydraulicTableErrorCode.InvalidRow, i,
                        "水力表存在空行或缺少 DN。");
                if (!Finite(band.LowerBound) || !Finite(band.UpperBound)
                    || band.LowerBound < 0 || band.UpperBound <= band.LowerBound + Epsilon)
                    return FailRow(result, HydraulicTableErrorCode.InvalidBounds, i,
                        "水力表区间上下界无效。");
            }

            List<string> duplicateDiameters = table.Bands
                .GroupBy(x => x.NominalDiameter.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(x => x.Count() > 1).Select(x => x.Key).ToList();
            if (duplicateDiameters.Count > 0)
                return Fail(result, HydraulicTableErrorCode.DuplicateDiameter,
                    "水力表 DN 不得重复：" + string.Join(",", duplicateDiameters));

            List<HydraulicTableBand> ordered = table.Bands
                .OrderBy(x => x.LowerBound).ThenBy(x => x.UpperBound).ToList();
            if (ordered[0].LowerBound > Epsilon || !ordered[0].LowerInclusive)
                return Fail(result, HydraulicTableErrorCode.Gap,
                    "水力表必须从 0 且包含下边界开始。");

            for (int i = 1; i < ordered.Count; i++)
            {
                HydraulicTableBand previous = ordered[i - 1];
                HydraulicTableBand current = ordered[i];
                if (current.LowerBound < previous.UpperBound - Epsilon
                    || (Math.Abs(current.LowerBound - previous.UpperBound) <= Epsilon
                        && current.LowerInclusive && previous.UpperInclusive))
                    return Fail(result, HydraulicTableErrorCode.Overlap,
                        "水力表区间存在重叠。");
                if (current.LowerBound > previous.UpperBound + Epsilon
                    || (Math.Abs(current.LowerBound - previous.UpperBound) <= Epsilon
                        && !current.LowerInclusive && !previous.UpperInclusive))
                    return Fail(result, HydraulicTableErrorCode.Gap,
                        "水力表区间存在空洞。");
            }

            result.IsValid = true;
            result.ErrorCode = HydraulicTableErrorCode.None;
            return result;
        }

        private static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static HydraulicTableValidationResult Fail(
            HydraulicTableValidationResult result,
            HydraulicTableErrorCode code,
            string message)
        {
            result.IsValid = false;
            result.ErrorCode = code;
            result.ErrorMessage = message;
            return result;
        }

        private static HydraulicTableValidationResult FailRow(
            HydraulicTableValidationResult result,
            HydraulicTableErrorCode code,
            int row,
            string message)
        {
            result.InvalidRows.Add(row);
            return Fail(result, code, message);
        }
    }

    public sealed class HydraulicTableLookup
    {
        private readonly HydraulicTableValidator validator = new HydraulicTableValidator();

        public HydraulicLookupResult Lookup(HydraulicTableDefinition table, double quantity)
        {
            HydraulicLookupResult result = new HydraulicLookupResult();
            HydraulicTableValidationResult validation = validator.Validate(table);
            if (!validation.IsValid)
                return Fail(result, validation.ErrorCode, validation.ErrorMessage);
            if (double.IsNaN(quantity) || double.IsInfinity(quantity) || quantity < 0)
                return Fail(result, HydraulicTableErrorCode.InvalidQuantity,
                    "查表量必须是大于或等于零的有限数值。");

            List<HydraulicTableBand> matches = table.Bands.Where(x =>
                (x.LowerInclusive ? quantity >= x.LowerBound - 1e-9 : quantity > x.LowerBound + 1e-9)
                && (x.UpperInclusive ? quantity <= x.UpperBound + 1e-9 : quantity < x.UpperBound - 1e-9))
                .ToList();
            if (matches.Count == 0)
                return Fail(result, HydraulicTableErrorCode.NoMatchingBand,
                    "查表量不在任何已配置区间内。");
            if (matches.Count > 1)
                return Fail(result, HydraulicTableErrorCode.AmbiguousMatch,
                    "查表量同时命中多个区间。");

            result.Success = true;
            result.ErrorCode = HydraulicTableErrorCode.None;
            result.Band = matches[0];
            return result;
        }

        private static HydraulicLookupResult Fail(
            HydraulicLookupResult result,
            HydraulicTableErrorCode code,
            string message)
        {
            result.Success = false;
            result.ErrorCode = code;
            result.ErrorMessage = message;
            return result;
        }
    }
}
