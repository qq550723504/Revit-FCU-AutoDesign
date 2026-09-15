using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI.Selection;

namespace FCUAutoDesign
{
    internal sealed class RoomDoorFilter : ISelectionFilter
    {
        private readonly Document document;
        private readonly ElementId roomId;

        public RoomDoorFilter(Document document, ElementId roomId)
        {
            this.document = document;
            this.roomId = roomId;
        }

        public bool AllowElement(Element element)
        {
            FamilyInstance door = element as FamilyInstance;
            if (door == null || element.Category == null
                || element.Category.Id.IntegerValue != (int)BuiltInCategory.OST_Doors)
                return false;

            Room room = document.GetElement(roomId) as Room;
            return RoomRuleSnapshotReader.BelongsToRoom(door, room);
        }

        public bool AllowReference(Reference reference, XYZ position)
        {
            return false;
        }
    }
}
