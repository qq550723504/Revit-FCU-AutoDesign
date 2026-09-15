using System;
using System.Collections.Generic;
using System.Linq;

namespace FCUAutoDesign.Business.EquipmentCatalog
{
    public enum FamilyTypeResolutionErrorCode
    {
        None,
        MissingSeries,
        MissingCatalogVersion,
        MissingModelCode,
        MissingMapping,
        AmbiguousMapping,
        InvalidMapping
    }

    public sealed class EquipmentCatalogDefinition
    {
        public string CatalogVersion { get; set; }
        public string SeriesId { get; set; }
        public IList<EquipmentCatalogMapping> Mappings { get; private set; }

        public EquipmentCatalogDefinition()
        {
            Mappings = new List<EquipmentCatalogMapping>();
        }
    }

    public sealed class EquipmentCatalogMapping
    {
        public string CatalogVersion { get; set; }
        public string SeriesId { get; set; }
        public string ModelCode { get; set; }
        public string RevitFamilyTypeKey { get; set; }
    }

    public sealed class FamilyTypeResolutionResult
    {
        public bool Success { get; internal set; }
        public FamilyTypeResolutionErrorCode ErrorCode { get; internal set; }
        public string ErrorMessage { get; internal set; }
        public EquipmentCatalogMapping Mapping { get; internal set; }
    }

    public sealed class FamilyTypeMappingResolver
    {
        public FamilyTypeResolutionResult Resolve(
            string catalogVersion,
            string seriesId,
            string modelCode,
            IEnumerable<EquipmentCatalogMapping> mappings)
        {
            FamilyTypeResolutionResult result = new FamilyTypeResolutionResult();
            if (string.IsNullOrWhiteSpace(seriesId))
                return Fail(result, FamilyTypeResolutionErrorCode.MissingSeries, "必须指定设备系列。");
            if (string.IsNullOrWhiteSpace(catalogVersion))
                return Fail(result, FamilyTypeResolutionErrorCode.MissingCatalogVersion, "必须指定设备目录版本。");
            if (string.IsNullOrWhiteSpace(modelCode))
                return Fail(result, FamilyTypeResolutionErrorCode.MissingModelCode, "必须指定设备型号。");

            List<EquipmentCatalogMapping> matches = (mappings ?? Enumerable.Empty<EquipmentCatalogMapping>())
                .Where(x => x != null
                    && string.Equals(x.CatalogVersion, catalogVersion, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.SeriesId, seriesId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.ModelCode, modelCode, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0)
                return Fail(result, FamilyTypeResolutionErrorCode.MissingMapping,
                    "目录型号没有显式的 Revit 族类型映射。");
            if (matches.Any(x => string.IsNullOrWhiteSpace(x.RevitFamilyTypeKey)))
                return Fail(result, FamilyTypeResolutionErrorCode.InvalidMapping,
                    "目录型号映射缺少 Revit 族类型键。");
            if (matches.Count > 1)
                return Fail(result, FamilyTypeResolutionErrorCode.AmbiguousMapping,
                    "目录型号存在多个 Revit 族类型映射，不能自动选择。");

            result.Success = true;
            result.ErrorCode = FamilyTypeResolutionErrorCode.None;
            result.Mapping = matches[0];
            return result;
        }

        private static FamilyTypeResolutionResult Fail(
            FamilyTypeResolutionResult result,
            FamilyTypeResolutionErrorCode code,
            string message)
        {
            result.Success = false;
            result.ErrorCode = code;
            result.ErrorMessage = message;
            return result;
        }
    }
}
