using Autodesk.Revit.DB.Architecture;

namespace FCUAutoDesign
{
    internal class FcuDesignResult
    {
        public System.Collections.Generic.List<FcuDesignResult> UnitResults { get; } = new System.Collections.Generic.List<FcuDesignResult>();
        public System.Collections.Generic.IEnumerable<FcuDesignResult> Devices
            => UnitResults.Count == 0 ? new[] { this } : (System.Collections.Generic.IEnumerable<FcuDesignResult>)UnitResults;
        public ExecutionOutcome Outcome { get; set; }
        public Autodesk.Revit.DB.XYZ ExpectedOutletDirection { get; set; }
        public double RoomAreaSqm { get; set; }
        public double CoolingLoadKw { get; set; }
        public int ActualDn { get; set; }
        public TeeConnectionResult SupplyConnection { get; set; }
        public TeeConnectionResult ReturnConnection { get; set; }
        public TeeConnectionResult DrainConnection { get; set; }
    }
}
