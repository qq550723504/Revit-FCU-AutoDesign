using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI.Selection;

namespace FCUAutoDesign
{
    public class PipeFilter : ISelectionFilter
    {
        private readonly MEPSystemClassification expectedClassification;

        public PipeFilter(MEPSystemClassification expectedClassification)
        {
            this.expectedClassification = expectedClassification;
        }

        public bool AllowElement(Element elem)
        {
            Pipe pipe = elem as Pipe;
            if (pipe == null) return false;
            ElementId typeId = pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId();
            PipingSystemType type = typeId == null ? null : pipe.Document.GetElement(typeId) as PipingSystemType;
            return type != null && type.SystemClassification == expectedClassification;
        }
        public bool AllowReference(Reference reference, XYZ position) => false;
    }
}
