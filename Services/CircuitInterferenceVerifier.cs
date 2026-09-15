using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace FCUAutoDesign
{
    internal static class CircuitInterferenceVerifier
    {
        public static void Verify(Document doc, TeeConnectionResult first, TeeConnectionResult second,
            string firstName, string secondName, bool includeMains = true)
        {
            if (first == null || second == null || !first.BranchCreated || !second.BranchCreated) return;
            List<ElementId> firstIds = Elements(first, includeMains);
            List<ElementId> secondIds = Elements(second, includeMains);
            foreach (ElementId id in firstIds.Concat(secondIds))
            {
                Element element = doc.GetElement(id);
                if (element == null || !ElementIntersectsFilter.IsElementSupported(element))
                    throw new InvalidOperationException($"元素 {id.IntegerValue} 无法进行实体干涉验证。");
            }
            foreach (ElementId id in firstIds)
            {
                using (ElementIntersectsElementFilter filter = new ElementIntersectsElementFilter(doc.GetElement(id)))
                using (FilteredElementCollector collector = new FilteredElementCollector(doc, secondIds))
                {
                    Element collision = collector.WherePasses(filter).FirstElement();
                    if (collision != null)
                        throw new InvalidOperationException(
                            $"{firstName}{Role(first, id)}元素 {id.IntegerValue} 与{secondName}{Role(second, collision.Id)}元素 "
                            + $"{collision.Id.IntegerValue} 实体相交。");
                }
            }
        }

        private static string Role(TeeConnectionResult result, ElementId id)
        {
            string detailedRole;
            if (result != null && result.ElementRoles.TryGetValue(id.IntegerValue, out detailedRole))
                return detailedRole;
            if (result != null && result.TeeCreated)
            {
                if (id == result.MainPart1Id || id == result.MainPart2Id) return "主管";
                if (id == result.MainPart1AdapterId || id == result.MainPart2AdapterId) return "主管过渡管件";
            }
            if (result != null && result.Chain.Any(x => x == id)) return "支管/管件";
            return "元素";
        }

        private static List<ElementId> Elements(TeeConnectionResult result, bool includeMains)
        {
            // 排除共享 FCU，包含管道、弯头、三通和自动过渡管件。
            List<ElementId> ids = result.Chain.Skip(1).ToList();
            if (result.TeeCreated)
            {
                if (includeMains)
                {
                    ids.Add(result.MainPart1Id);
                    ids.Add(result.MainPart2Id);
                }
                if (result.MainPart1AdapterId != null) ids.Add(result.MainPart1AdapterId);
                if (result.MainPart2AdapterId != null) ids.Add(result.MainPart2AdapterId);
            }
            return ids.Distinct().ToList();
        }
    }
}
