using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Plumbing;
using static FCUAutoDesign.RevitUnits;

namespace FCUAutoDesign
{
    internal class FcuDesignService
    {
        private readonly FcuPlacementService placement = new FcuPlacementService();
        private readonly FcuConnectorResolver connectors = new FcuConnectorResolver();
        private readonly HydronicSeparationService separation = new HydronicSeparationService();
        private readonly CondensateSeparationService condensate = new CondensateSeparationService();
        private readonly ConnectionChainVerifier verifier = new ConnectionChainVerifier();

        // 唯一的主事务/事务组所有者；提交后的验证失败仍回滚整个事务组。
        public FcuDesignResult Execute(Document doc, Room room, Pipe supplyMainPipe,
            Pipe returnMainPipe, Pipe condensateMainPipe, FcuDesignOptions options, RoomBatchContext batch = null)
        {
            double roomAreaSqm = room.Area * SQFT_TO_SQM;
            double coolingLoadKw = roomAreaSqm * 0.160; // 160 W/m² 指标估算
            int actualDn = 20;
            if (options.EnableAutoSizing)
            {
                actualDn = (roomAreaSqm < 25.0) ? 20 : 25; // <25㎡ 选 DN20，≥25㎡ 选 DN25
            }
            double branchDiameterFeet = actualDn * MM_TO_FEET;
            PipeSystemType condensateType = PipeSystemType.Sanitary;
            if (options.EnableCondensate)
            {
                ElementId systemId = condensateMainPipe?.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId();
                PipingSystemType system = systemId == null ? null : doc.GetElement(systemId) as PipingSystemType;
                PipeSystemType? expected = system == null ? null
                    : CondensateSystemPolicy.ConnectorTypeFor(system.SystemClassification);
                if (!expected.HasValue)
                    throw new InvalidOperationException("请选择 卫生设备（Sanitary）分类的冷凝水主管。");
                condensateType = expected.Value;
            }

            // 执行结果诊断追踪器（用于杜绝假成功并诚实汇报）
            ExecutionOutcome outcome = new ExecutionOutcome { CondensateEnabled = options.EnableCondensate };
            outcome.Warnings.Add(options.MinimumStraightLengthMm.HasValue
                ? $"三路首段净直管最小值 {options.MinimumStraightLengthMm.Value:F1} mm，按用户输入校验；不代表阀组/保温/检修空间已验收。"
                : "未指定工程最小净直管长度；禁止缩短目标首段，但未校验安装净长要求。400 mm 默认值不是规范值。");
            outcome.Warnings.Add("仅检查宿主墙的水平正交穿越和墙内管件；未创建洞口或套管，未检查链接模型、保温及施工净距。");
            CondensateDrainResult drainResult = null;
            TeeConnectionResult supplyResult = null;
            TeeConnectionResult returnResult = null;
            XYZ expectedOutletDirection = null;

            using (TransactionGroup group = new TransactionGroup(doc, "FCU PoC 验证"))
            {
                group.Start();
                using (Transaction trans = new Transaction(doc, "FCU 智能布设与下翻绕管闭环"))
                {
                    trans.Start();
                    // 保留警告供用户查看；提交遇到错误时回滚，避免进入待决状态后报成功。
                    FailureHandlingOptions failOpt = trans.GetFailureHandlingOptions();
                    RollBackOnErrorPreprocessor failureReporter = new RollBackOnErrorPreprocessor();
                    failOpt.SetFailuresPreprocessor(failureReporter);
                    failOpt.SetClearAfterRollback(true);
                    trans.SetFailureHandlingOptions(failOpt);

                    FcuPlacementResult placed = placement.Place(doc, room, options);
                    FamilyInstance fcu = placed.Instance;
                    expectedOutletDirection = placed.ExpectedOutletDirection;
                    outcome.PlacementVerified = true;
                    outcome.FcuId = fcu.Id;
                    outcome.PlacementPoint = placed.Point;

                    // 先识别设备接口，再按启用的回路执行接管。
                    FCUConnectors conns = connectors.DetectFCUConnectors(fcu, condensateType);
                    if (conns.SupplyConnector == null)
                        throw new InvalidOperationException("FCU 缺少明确分类的供水接口。");
                    if (options.EnableReturnPipe && returnMainPipe != null && conns.ReturnConnector == null)
                        throw new InvalidOperationException("FCU 缺少明确分类的回水接口。");
                    if (options.EnableCondensate && conns.CondensateConnector == null)
                        outcome.Warnings.Add($"FCU 没有匹配的 {condensateType} 管道端接口，未生成冷凝水管。"
                            + "设备实际接口：" + connectors.DescribeConnectors(fcu));

                    // 步骤 C: 供水支管下翻避让并接入主管（含 SubTransaction 保护）
                    if (conns.SupplyConnector != null && supplyMainPipe != null)
                    {
                        TeeConnectionResult supplyRes = separation.ConnectReturn(
                            doc,
                            conns.SupplyConnector,
                            supplyMainPipe,
                            branchDiameterFeet,
                            options.FlipDropMm * MM_TO_FEET,
                            options.ValveClearanceMm * MM_TO_FEET,
                            options.BreakCurveAndTee, failureReporter, null, batch?.Supply, batch, "供水",
                            options.MinimumStraightLengthMm * MM_TO_FEET
                        );
                        outcome.SupplyBranchCreated = supplyRes.BranchCreated;
                        outcome.SupplyTeeConnected = supplyRes.TeeCreated;
                        supplyResult = supplyRes;
                        if (!string.IsNullOrEmpty(supplyRes.ErrorMessage))
                        {
                            outcome.Warnings.Add("供水管: " + supplyRes.ErrorMessage);
                        }
                    }

                    // 供水候选可能回滚；重新读取回水接口。
                    conns = connectors.DetectFCUConnectors(doc.GetElement(outcome.FcuId) as FamilyInstance, condensateType);
                    // 步骤 D: 回水支管下翻避让并接入回水主管
                    if (options.EnableReturnPipe && returnMainPipe != null && conns.ReturnConnector != null)
                    {
                        TeeConnectionResult returnRes = separation.ConnectReturn(
                            doc,
                            conns.ReturnConnector,
                            returnMainPipe,
                            branchDiameterFeet,
                            options.FlipDropMm * MM_TO_FEET,
                            options.ValveClearanceMm * MM_TO_FEET,
                            options.BreakCurveAndTee, failureReporter, supplyResult, batch?.Return, batch,
                            minimumStraightLength: options.MinimumStraightLengthMm * MM_TO_FEET
                        );
                        outcome.ReturnBranchCreated = returnRes.BranchCreated;
                        outcome.ReturnTeeConnected = returnRes.TeeCreated;
                        returnResult = returnRes;
                        if (!string.IsNullOrEmpty(returnRes.ErrorMessage))
                        {
                            outcome.Warnings.Add("回水管: " + returnRes.ErrorMessage);
                        }
                    }

                    // 步骤 E: 按设备与主管实际标高连接冷凝水主管
                    // 回水候选可能经历回滚；重新读取设备接口，避免沿用旧 Connector 包装对象。
                    conns = connectors.DetectFCUConnectors(doc.GetElement(outcome.FcuId) as FamilyInstance, condensateType);
                    if (options.EnableCondensate && conns.CondensateConnector != null)
                    {
                        drainResult = condensate.Connect(
                            doc, conns.CondensateConnector, condensateMainPipe, room.Level.Id,
                            20 * MM_TO_FEET,
                            failureReporter, supplyResult, returnResult, batch?.Condensate, batch,
                            options.ValveClearanceMm * MM_TO_FEET,
                            options.FlipDropMm * MM_TO_FEET, options.MinimumStraightLengthMm * MM_TO_FEET);
                        outcome.CondensateConnected = drainResult.Connected;
                        if (!string.IsNullOrEmpty(drainResult.ErrorMessage))
                            outcome.Warnings.Add("冷凝水管: " + drainResult.ErrorMessage);
                    }

                    // 提交主事务
                    TransactionStatus commitStatus = trans.Commit();
                    if (commitStatus != TransactionStatus.Committed)
                    {
                        string details = failureReporter.Errors.Count > 0
                            ? string.Join(Environment.NewLine, failureReporter.Errors)
                            : "Revit 未提供可捕获的 Error 详情；请检查此前显示的 Revit 提示。";
                        throw new InvalidOperationException(
                            $"Revit 主事务未成功提交（状态：{commitStatus}），本次结果不计为成功。"
                            + Environment.NewLine + details
                            + Environment.NewLine + "元素 ID 为失败时的记录；回滚后新建元素可能已不存在。");
                    }
                }

                // 提交后重新读取元素与接口，避免用提交前的布尔值证明最终模型状态。
                FamilyInstance committedFcu = doc.GetElement(outcome.FcuId) as FamilyInstance;
                placement.VerifySupplyOutletDirection(committedFcu, expectedOutletDirection);
                LocationPoint committedLocation = committedFcu?.Location as LocationPoint;
                if (committedLocation == null || !room.IsPointInRoom(committedLocation.Point)
                    || committedLocation.Point.DistanceTo(outcome.PlacementPoint) > MM_TO_FEET
                    || (supplyResult != null && !verifier.VerifyConnectionChain(doc, supplyResult))
                    || (returnResult != null && !verifier.VerifyConnectionChain(doc, returnResult)))
                    throw new InvalidOperationException("提交后位置或连接链复核失败，已回滚整次 PoC 操作。");
                condensate.Verify(doc, drainResult, supplyResult, returnResult);
                separation.Verify(doc, supplyResult, returnResult);
                ConnectionInstallationVerifier.Verify(doc, supplyResult);
                ConnectionInstallationVerifier.Verify(doc, returnResult);
                ConnectionInstallationVerifier.Verify(doc, drainResult?.Connected == true ? drainResult.Connection : null);
                batch?.VerifyPrevious(doc, supplyResult, returnResult, drainResult?.Connected == true ? drainResult.Connection : null);
                if (group.Assimilate() != TransactionStatus.Committed)
                    throw new InvalidOperationException("PoC 事务组未成功提交。");
            }

            return new FcuDesignResult
            {
                Outcome = outcome, RoomAreaSqm = roomAreaSqm,
                CoolingLoadKw = coolingLoadKw, ActualDn = actualDn,
                SupplyConnection = supplyResult, ReturnConnection = returnResult,
                DrainConnection = drainResult?.Connected == true ? drainResult.Connection : null
            };
        }
    }
}
