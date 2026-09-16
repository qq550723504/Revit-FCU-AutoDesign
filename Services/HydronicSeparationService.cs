using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using static FCUAutoDesign.RevitUnits;

namespace FCUAutoDesign
{
    internal class HydronicSeparationService
    {
        private readonly HydronicConnectionService hydronic = new HydronicConnectionService();

        // 保留供水，逐个尝试回水预留段/下翻高度；失败候选完整回滚后才能尝试下一条。
        public TeeConnectionResult ConnectReturn(Document doc, Connector connector, Pipe main,
            double diameter, double drop, double lead, bool enableTee,
            RollBackOnErrorPreprocessor reporter, TeeConnectionResult supply,
            MainPipeRun run = null, RoomBatchContext batch = null, string circuit = "回水",
            double? minimumStraightLength = null)
        {
            string lastReason = "没有可用路线";
            ElementId mainId = main.Id;
            ElementId ownerId = connector.Owner.Id;
            int connectorId = connector.Id;
            double step = Math.Max(100 * MM_TO_FEET, diameter * 4);
            int attempts = 0;
            // 先穷举有限的无下翻候选，再尝试有高差的避让，避免默认制造低点。
            foreach (bool direct in new[] { true, false })
            for (int leadStep = 0; leadStep <= 4; leadStep++)
            {
                for (int dropStep = 0; dropStep <= (direct ? 0 : 4); dropStep++)
                {
                    double candidateDrop = direct ? 0 : drop + dropStep * step;
                    attempts++;
                    HashSet<int> originalRoles = new HashSet<int>(reporter.ElementRoles.Keys);
                    using (SubTransaction candidate = new SubTransaction(doc))
                    {
                        if (candidate.Start() != TransactionStatus.Started)
                            throw new InvalidOperationException("回水避让候选事务未能启动。");
                        try
                        {
                            TeeConnectionResult result = hydronic.ConnectWithLowerFlipAndTee(doc,
                                connector, main, diameter, candidateDrop,
                                lead + leadStep * step, enableTee, circuit, reporter, run,
                                minimumStraightLength: minimumStraightLength);
                            if (!result.BranchCreated || (enableTee && !result.TeeCreated))
                                throw new InvalidOperationException(result.ErrorMessage ?? "回水连接未完成。");
                            doc.Regenerate();
                            Verify(doc, supply, result);
                            batch?.VerifyNew(doc, result);
                            ConnectionInstallationVerifier.Verify(doc, result);
                            if (candidate.Commit() != TransactionStatus.Committed)
                                throw new InvalidOperationException("回水避让候选未成功提交。");
                            result.ErrorMessage = $"{circuit}接管目标距离采用 {(lead + leadStep * step) * FEET_TO_MM:F0} mm，"
                                    + $"设备侧下翻高度 {candidateDrop * FEET_TO_MM:F0} mm。"
                                    + (enableTee ? "" : "未启用主管三通接入。");
                            return result;
                        }
                        catch (Autodesk.Revit.Exceptions.RegenerationFailedException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            lastReason = ex.Message;
                            if (candidate.GetStatus() == TransactionStatus.Started
                                && candidate.RollBack() != TransactionStatus.RolledBack)
                                throw new InvalidOperationException("回水避让候选回滚失败，必须取消整次操作。", ex);
                        }
                    }
                    foreach (int id in reporter.ElementRoles.Keys.Where(id => !originalRoles.Contains(id)).ToList())
                        reporter.ElementRoles.Remove(id);
                    // 回滚之后从文档重新获取后续会使用的模型对象/接口。
                    main = doc.GetElement(mainId) as Pipe;
                    FamilyInstance owner = doc.GetElement(ownerId) as FamilyInstance;
                    connector = owner.MEPModel.ConnectorManager.Connectors.Cast<Connector>()
                        .Single(c => c.Id == connectorId);
                }
            }
            throw new InvalidOperationException($"在限定的 {attempts} 条{circuit}候选路线内未找到可接入且不与已生成管线相交的方案。"
                + "已取消本次操作，请调整设备位置、主管或路径参数。最后原因：" + lastReason);
        }

        // 使用 Revit 实体干涉过滤器检查本次两路的管道和管件，不把共享 FCU 当成障碍物。
        public void Verify(Document doc, TeeConnectionResult supply, TeeConnectionResult ret)
        {
            if (supply == null || ret == null || !supply.BranchCreated || !ret.BranchCreated) return;
            CircuitInterferenceVerifier.Verify(doc, supply, ret, "供水", "回水");
        }

    }
}
