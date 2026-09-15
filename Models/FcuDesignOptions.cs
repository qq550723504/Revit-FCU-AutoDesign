using Autodesk.Revit.DB.Plumbing;

namespace FCUAutoDesign
{
    internal class FcuDesignOptions
    {
        public int SelectedFcuTypeId { get; set; }
        public double DoorOffsetMm { get; set; }
        public double FcuElevationMm { get; set; }
        public double ValveClearanceMm { get; set; }
        public double FlipDropMm { get; set; }
        public double CoolingIndexWPerSquareMeter { get; set; }
        public bool EnableAutoSizing { get; set; }
        public bool EnableBusinessRulePreview { get; set; }
        public System.Collections.Generic.IDictionary<int, Autodesk.Revit.DB.ElementId> SelectedDoorIds { get; set; }
        public bool EnableReturnPipe { get; set; }
        public bool EnableCondensate { get; set; }
        public bool BreakCurveAndTee { get; set; }
    }
}
