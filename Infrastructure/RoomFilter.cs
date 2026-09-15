using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI.Selection;

namespace FCUAutoDesign
{
    public class RoomFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem) => elem is Room;
        public bool AllowReference(Reference reference, XYZ position) => false;
    }
}
