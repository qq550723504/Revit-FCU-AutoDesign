using Autodesk.Revit.DB.Architecture;

namespace FCUAutoDesign
{
    internal class FcuDesignResult
    {
        public ExecutionOutcome Outcome { get; set; }
        public double RoomAreaSqm { get; set; }
        public double CoolingLoadKw { get; set; }
        public int ActualDn { get; set; }
        public TeeConnectionResult SupplyConnection { get; set; }
        public TeeConnectionResult ReturnConnection { get; set; }
        public TeeConnectionResult DrainConnection { get; set; }
    }
}
