using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace FCUAutoDesign
{
    internal class CondensateDrainResult
    {
        public bool Connected { get; set; }
        public string ErrorMessage { get; set; }
        public TeeConnectionResult Connection { get; } = new TeeConnectionResult();
        public List<ElementId> PipeIds { get; } = new List<ElementId>();
        public List<int> StartConnectorIds { get; } = new List<int>();
        public List<int> EndConnectorIds { get; } = new List<int>();
        public List<XYZ> PlannedDirections { get; } = new List<XYZ>();
        public double MinLength { get; set; }
    }
}
