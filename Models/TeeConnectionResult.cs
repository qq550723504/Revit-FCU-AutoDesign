using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace FCUAutoDesign
{
    internal class TeeConnectionResult
    {
        public bool BranchCreated { get; set; }
        public bool TeeCreated { get; set; }
        public string ErrorMessage { get; set; }
        public int FcuConnectorId { get; set; }
        public List<ElementId> Chain { get; } = new List<ElementId>();
        public ElementId MainPart1Id { get; set; }
        public ElementId MainPart2Id { get; set; }
        public ElementId MainPart1AdapterId { get; set; }
        public ElementId MainPart2AdapterId { get; set; }
    }
}
