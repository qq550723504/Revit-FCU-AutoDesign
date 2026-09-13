using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;

namespace FCUAutoDesign
{
    internal static class CondensateSystemPolicy
    {
        // 冷凝水主管和设备管道端接口统一要求卫生设备（Sanitary）分类。
        public static bool IsCandidate(PipeSystemType actual, PipeSystemType expected)
        {
            return expected == PipeSystemType.Sanitary && actual == PipeSystemType.Sanitary;
        }

        public static PipeSystemType? ConnectorTypeFor(MEPSystemClassification classification)
        {
            if (classification == MEPSystemClassification.Sanitary) return PipeSystemType.Sanitary;
            return null;
        }
    }
}
