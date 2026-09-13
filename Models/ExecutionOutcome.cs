using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace FCUAutoDesign
{
    internal class ExecutionOutcome
    {
        public ElementId FcuId { get; set; }
        public XYZ PlacementPoint { get; set; }
        public bool PlacementVerified { get; set; }
        public bool SupplyBranchCreated { get; set; }
        public bool SupplyTeeConnected { get; set; }
        public bool ReturnBranchCreated { get; set; }
        public bool ReturnTeeConnected { get; set; }
        public bool CondensateEnabled { get; set; }
        public bool CondensateConnected { get; set; }
        public List<string> Warnings { get; } = new List<string>();
    }
}
