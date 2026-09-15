using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace FCUAutoDesign
{
    internal class FcuTypeCatalog
    {
        public List<FamilySymbol> GetFCUSymbols(Document doc)
        {
            List<FamilySymbol> candidates = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_MechanicalEquipment)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(x => x.FamilyName.Contains("风机盘管")
                                  || x.FamilyName.Contains("FCU")
                                  || x.Name.Contains("FP")
                                  || x.FamilyName.Contains("Fan Coil")).ToList();
            return candidates.OrderBy(x => x.FamilyName).ThenBy(x => x.Name).ToList();
        }
    }
}
