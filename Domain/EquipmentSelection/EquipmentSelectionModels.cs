using System;
using System.Collections.Generic;

namespace FCUAutoDesign.Business.EquipmentSelection
{
    public enum EquipmentSelectionErrorCode
    {
        None,
        MissingSeries,
        MissingCatalogVersion,
        InvalidLength,
        InvalidWidth,
        InvalidCoolingIndex,
        PlacementOffsetOutsideRoom,
        LengthOutsideSupportedRange,
        ZeroLoadRequiresConfirmation,
        EmptyCatalog,
        InvalidCatalogEntry,
        CapacityNotFound,
        AmbiguousCapacityMatch
    }

    public sealed class EquipmentSelectionInput
    {
        public string RoomUniqueId { get; set; }
        public string SeriesId { get; set; }
        public string CatalogVersion { get; set; }
        public double LengthM { get; set; }
        public double WidthM { get; set; }
        public double CoolingIndexWPerSquareMeter { get; set; }
        public double BaseElevationM { get; set; }
        public double InstallationHeightM { get; set; }
        public double PlacementOffsetM { get; set; }

        public static EquipmentSelectionInput CreateDefault()
        {
            return new EquipmentSelectionInput
            {
                CoolingIndexWPerSquareMeter = 200.0,
                InstallationHeightM = 2.5,
                PlacementOffsetM = 0.5
            };
        }
    }

    public sealed class EquipmentCatalogEntry
    {
        public string SeriesId { get; set; }
        public string ModelCode { get; set; }
        public double RatedCoolingCapacityKw { get; set; }
        public double RatedHeatingCapacityKw { get; set; }
        public double PowerW { get; set; }
        public int SupplyReturnNominalDiameterMm { get; set; }
        public int CondensateNominalDiameterMm { get; set; }
        public string RevitFamilyTypeKey { get; set; }
        public int? SelectionPriority { get; set; }
    }

    public sealed class EquipmentSelectionResult
    {
        public bool Success { get; internal set; }
        public EquipmentSelectionErrorCode ErrorCode { get; internal set; }
        public string ErrorMessage { get; internal set; }
        public string Explanation { get; internal set; }
        public double AreaSquareMeters { get; internal set; }
        public double DesignLoadKw { get; internal set; }
        public int UnitCount { get; internal set; }
        public double UnitDesignLoadKw { get; internal set; }
        public EquipmentCatalogEntry SelectedEquipment { get; internal set; }
        public IList<EquipmentCatalogEntry> CapacityCandidates { get; internal set; }
        public IList<EquipmentPlacementPoint> PlacementPoints { get; internal set; }

        public EquipmentSelectionResult()
        {
            CapacityCandidates = new List<EquipmentCatalogEntry>();
            PlacementPoints = new List<EquipmentPlacementPoint>();
        }
    }

    public sealed class EquipmentPlacementPoint
    {
        public int Sequence { get; internal set; }
        public double XAlongLengthM { get; internal set; }
        public double YFromReferenceEdgeM { get; internal set; }
        public double AbsoluteElevationM { get; internal set; }
    }
}
