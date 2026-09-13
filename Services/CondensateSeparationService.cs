using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using static FCUAutoDesign.RevitUnits;

namespace FCUAutoDesign
{
    internal class CondensateSeparationService
    {
        private readonly CondensateDrainService drain = new CondensateDrainService();

        public CondensateDrainResult Connect(Document doc, Connector connector, Pipe main,
            ElementId levelId, double diameter, RollBackOnErrorPreprocessor reporter,
            TeeConnectionResult supply, TeeConnectionResult ret)
        {
            ElementId ownerId = connector.Owner.Id;
            int connectorId = connector.Id;
            ElementId mainId = main.Id;
            string lastReason = "没有可用路线";
            double step = Math.Max(100 * MM_TO_FEET, diameter * 4);
            int[] offsets = { 0, -1, 1, -2, 2 };
            for (int leadStep = 0; leadStep <= 4; leadStep++)
            {
                foreach (int offset in offsets)
                {
                    HashSet<int> originalRoles = new HashSet<int>(reporter.ElementRoles.Keys);
                    using (SubTransaction candidate = new SubTransaction(doc))
                    {
                        if (candidate.Start() != TransactionStatus.Started)
                            throw new InvalidOperationException("冷凝水避让候选事务未能启动。");
                        try
                        {
                            FamilyInstance fcu = doc.GetElement(ownerId) as FamilyInstance;
                            Connector current = fcu.MEPModel.ConnectorManager.Connectors.Cast<Connector>()
                                .Single(c => c.Id == connectorId);
                            double lead = 400 * MM_TO_FEET + leadStep * step;
                            CondensateDrainResult result = drain.ConnectToMain(doc, current,
                                doc.GetElement(mainId) as Pipe, levelId, diameter, reporter, lead, offset * step);
                            if (!result.Connected) throw new InvalidOperationException(result.ErrorMessage);
                            doc.Regenerate();
                            Verify(doc, result, supply, ret);
                            if (candidate.Commit() != TransactionStatus.Committed)
                                throw new InvalidOperationException("冷凝水避让候选未成功提交。");
                            if (leadStep != 0 || offset != 0)
                                result.ErrorMessage = $"为避开供回水，冷凝水预留长度采用 {lead * FEET_TO_MM:F0} mm，"
                                    + $"中间转折点相对原路线高度偏移 {offset * step * FEET_TO_MM:F0} mm。";
                            return result;
                        }
                        catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                        catch (Exception ex)
                        {
                            lastReason = ex.Message;
                            if (candidate.GetStatus() == TransactionStatus.Started
                                && candidate.RollBack() != TransactionStatus.RolledBack)
                                throw new InvalidOperationException("冷凝水候选回滚失败，必须取消整次操作。", ex);
                        }
                    }
                    foreach (int id in reporter.ElementRoles.Keys.Where(id => !originalRoles.Contains(id)).ToList())
                        reporter.ElementRoles.Remove(id);
                }
            }
            return new CondensateDrainResult
            {
                ErrorMessage = "25 条冷凝水候选均未通过接管与供回水干涉检查；未保留本次冷凝水操作。最后原因：" + lastReason
            };
        }

        public void Verify(Document doc, CondensateDrainResult result,
            TeeConnectionResult supply, TeeConnectionResult ret)
        {
            if (result == null || !result.Connected) return;
            drain.VerifyConnection(doc, result);
            CircuitInterferenceVerifier.Verify(doc, supply, result.Connection, "供水", "冷凝水");
            CircuitInterferenceVerifier.Verify(doc, ret, result.Connection, "回水", "冷凝水");
        }
    }
}
