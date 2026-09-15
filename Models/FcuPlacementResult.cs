using Autodesk.Revit.DB;

namespace FCUAutoDesign
{
    internal class FcuPlacementResult
    {
        public FamilyInstance Instance { get; set; }
        public XYZ Point { get; set; }
        public XYZ ExpectedOutletDirection { get; set; }
    }
}
