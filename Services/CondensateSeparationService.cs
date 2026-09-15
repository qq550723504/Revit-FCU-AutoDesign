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
        private const int MaxCandidateStep = 8;
        private readonly HydronicConnectionService connection = new HydronicConnectionService();
        private readonly ConnectionChainVerifier verifier = new ConnectionChainVerifier();

        // 冷凝水与供回水采用同一条“水平预留→下翻→横向→竖直接入→三通”路径。
        // 区别只在系统分类为 Sanitary，以及使用冷凝水支管管径。
        public CondensateDrainResult Connect(Document doc, Connector connector, Pipe main,
            ElementId levelId, double diameter, RollBackOnErrorPreprocessor reporter,
            TeeConnectionResult supply, TeeConnectionResult ret, MainPipeRun run = null,
            RoomBatchContext batch = null, double leadLength = 500 * MM_TO_FEET,
            double drop = 150 * MM_TO_FEET)
        {
            if (connector == null) throw new InvalidOperationException("FCU 没有可用的冷凝水管道接口。");
            if (main == null) throw new InvalidOperationException("未选择冷凝水主管。");
            if (levelId == null || !(doc.GetElement(levelId) is Level))
                throw new InvalidOperationException("目标房间没有有效的基准标高。");
            if (double.IsNaN(diameter) || double.IsInfinity(diameter) || diameter <= 0)
                throw new InvalidOperationException("冷凝水管径必须为有限正数。");
            if (double.IsNaN(leadLength) || double.IsInfinity(leadLength) || leadLength <= 0
                || double.IsNaN(drop) || double.IsInfinity(drop) || drop <= 0)
                throw new InvalidOperationException("冷凝水预留段和下翻高度必须为有限正数。");

            string lastReason = "没有可用路线";
            ElementId mainId = main.Id;
            ElementId ownerId = connector.Owner.Id;
            int connectorId = connector.Id;
            double step = Math.Max(100 * MM_TO_FEET, diameter * 4);
            int[] lateralSteps = { 0, -1, 1, -2, 2 };

            foreach (int lateralStep in lateralSteps)
            {
                int maxDropStep = lateralStep == 0 ? MaxCandidateStep : 4;
                for (int leadStep = 0; leadStep <= MaxCandidateStep; leadStep++)
                {
                    for (int dropStep = 0; dropStep <= maxDropStep; dropStep++)
                    {
                        double candidateLead = leadLength + leadStep * step;
                        double candidateDrop = drop + dropStep * step;
                        double candidateLateral = lateralStep * step;
                        HashSet<int> originalRoles = new HashSet<int>(reporter.ElementRoles.Keys);
                        using (SubTransaction candidate = new SubTransaction(doc))
                        {
                            if (candidate.Start() != TransactionStatus.Started)
                                throw new InvalidOperationException("冷凝水避让候选事务未能启动。");
                            try
                            {
                                FamilyInstance fcu = doc.GetElement(ownerId) as FamilyInstance;
                                Connector current = fcu?.MEPModel?.ConnectorManager?.Connectors.Cast<Connector>()
                                    .SingleOrDefault(c => c.Id == connectorId);
                                Pipe currentMain = doc.GetElement(mainId) as Pipe;
                                TeeConnectionResult connectionResult = connection.ConnectWithLowerFlipAndTee(
                                    doc, current, currentMain, diameter, candidateDrop, candidateLead, true,
                                    "冷凝水", reporter, run, MEPSystemClassification.Sanitary,
                                    candidateLateral);
                                if (!connectionResult.BranchCreated || !connectionResult.TeeCreated)
                                    throw new InvalidOperationException(connectionResult.ErrorMessage ?? "冷凝水连接未完成。");

                                doc.Regenerate();
                                Verify(doc, connectionResult, supply, ret);
                                batch?.VerifyNew(doc, connectionResult);
                                if (candidate.Commit() != TransactionStatus.Committed)
                                    throw new InvalidOperationException("冷凝水避让候选未成功提交。");

                                CondensateDrainResult result = Wrap(doc, connectionResult);
                                if (leadStep != 0 || dropStep != 0 || lateralStep != 0)
                                    result.ErrorMessage = $"冷凝水采用避让路径，预留段 {candidateLead * FEET_TO_MM:F0} mm，"
                                        + $"下翻高度 {candidateDrop * FEET_TO_MM:F0} mm，设备侧横移 {candidateLateral * FEET_TO_MM:F0} mm。";
                                return result;
                            }
                            catch (Autodesk.Revit.Exceptions.RegenerationFailedException)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                lastReason = $"预留 {candidateLead * FEET_TO_MM:F0} mm，下翻 {candidateDrop * FEET_TO_MM:F0} mm，"
                                    + $"设备侧横移 {candidateLateral * FEET_TO_MM:F0} mm：{ex.Message}";
                                if (candidate.GetStatus() == TransactionStatus.Started
                                    && candidate.RollBack() != TransactionStatus.RolledBack)
                                    throw new InvalidOperationException("冷凝水候选回滚失败，必须取消整次操作。", ex);
                            }
                        }
                        foreach (int id in reporter.ElementRoles.Keys.Where(id => !originalRoles.Contains(id)).ToList())
                            reporter.ElementRoles.Remove(id);

                        // 回滚后重新读取主管、设备和接口，不能继续使用已失效的 Connector 包装对象。
                        main = doc.GetElement(mainId) as Pipe;
                        FamilyInstance owner = doc.GetElement(ownerId) as FamilyInstance;
                        connector = owner?.MEPModel?.ConnectorManager?.Connectors.Cast<Connector>()
                            .SingleOrDefault(c => c.Id == connectorId);
                    }
                }
            }

            return new CondensateDrainResult
            {
                ErrorMessage = $"{(MaxCandidateStep + 1) * (MaxCandidateStep + 1) + 4 * (MaxCandidateStep + 1) * 5} 条冷凝水避让候选均未通过接管与供回水干涉检查；未保留本次冷凝水操作。最后原因：{lastReason}"
            };
        }

        private void Verify(Document doc, TeeConnectionResult result,
            TeeConnectionResult supply, TeeConnectionResult ret)
        {
            if (result == null || !result.BranchCreated || !result.TeeCreated) return;
            string failure;
            if (!verifier.VerifyConnectionChain(doc, result, out failure))
                throw new InvalidOperationException("冷凝水连接验证失败：" + failure);
            CircuitInterferenceVerifier.Verify(doc, supply, result, "供水", "冷凝水");
            CircuitInterferenceVerifier.Verify(doc, ret, result, "回水", "冷凝水");
        }

        public void Verify(Document doc, CondensateDrainResult result,
            TeeConnectionResult supply, TeeConnectionResult ret)
        {
            if (result == null || !result.Connected) return;
            Verify(doc, result.Connection, supply, ret);
        }

        private static CondensateDrainResult Wrap(Document doc, TeeConnectionResult source)
        {
            CondensateDrainResult result = new CondensateDrainResult
            {
                Connected = source.BranchCreated && source.TeeCreated,
                ErrorMessage = source.ErrorMessage
            };
            result.Connection.BranchCreated = source.BranchCreated;
            result.Connection.TeeCreated = source.TeeCreated;
            result.Connection.ErrorMessage = source.ErrorMessage;
            result.Connection.FcuConnectorId = source.FcuConnectorId;
            result.Connection.MainPart1Id = source.MainPart1Id;
            result.Connection.MainPart2Id = source.MainPart2Id;
            result.Connection.MainPart1AdapterId = source.MainPart1AdapterId;
            result.Connection.MainPart2AdapterId = source.MainPart2AdapterId;
            foreach (KeyValuePair<int, string> role in source.ElementRoles)
                result.Connection.ElementRoles[role.Key] = role.Value;
            result.Connection.Chain.AddRange(source.Chain);
            foreach (ElementId id in source.Chain)
                if (doc.GetElement(id) is Pipe)
                    result.PipeIds.Add(id);
            return result;
        }
    }
}
