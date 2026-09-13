using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace FCUAutoDesign
{
    public class RollBackOnErrorPreprocessor : IFailuresPreprocessor
    {
        // 必须在回滚清除 FailureMessageAccessor 前复制为普通字符串。
        public List<string> Errors { get; } = new List<string>();
        public Dictionary<int, string> ElementRoles { get; } = new Dictionary<int, string>();

        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            IList<FailureMessageAccessor> failureMessages = failuresAccessor.GetFailureMessages();
            bool hasError = false;
            foreach (FailureMessageAccessor failure in failureMessages)
            {
                if (failure.GetSeverity() == FailureSeverity.Error)
                {
                    hasError = true;
                    string elementIds = string.Join(", ", failure.GetFailingElementIds().Select(id =>
                        ElementRoles.TryGetValue(id.IntegerValue, out string role)
                            ? $"{id.IntegerValue}（{role}）" : id.IntegerValue.ToString()));
                    string detail = failure.GetDescriptionText()
                        + $" [FailureId: {failure.GetFailureDefinitionId().Guid}; 元素 ID: {elementIds}]";
                    if (!Errors.Contains(detail)) Errors.Add(detail);
                }
            }
            return hasError ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }
}
