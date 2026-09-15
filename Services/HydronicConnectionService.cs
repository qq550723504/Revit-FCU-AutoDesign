using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using static FCUAutoDesign.RevitUnits;

namespace FCUAutoDesign
{
    internal class HydronicConnectionService
    {
        private readonly ConnectionChainVerifier verifier = new ConnectionChainVerifier();

        // 主事务由 FcuDesignService 持有，三通失败只回滚局部子事务。
        public TeeConnectionResult ConnectWithLowerFlipAndTee(
            Document doc,
            Connector fcuConn,
            Pipe mainPipe,
            double branchDia,
            double flipDrop,
            double valveClearance,
            bool enableTeeFitting,
            string circuit,
            RollBackOnErrorPreprocessor failureReporter, MainPipeRun run = null,
            MEPSystemClassification? expectedClassificationOverride = null)
        {
            if (run != null)
                mainPipe = run.Resolve(doc, fcuConn.Origin + fcuConn.CoordinateSystem.BasisZ * valveClearance);
            failureReporter.ElementRoles[mainPipe.Id.IntegerValue] = circuit + "主管";
            TeeConnectionResult result = new TeeConnectionResult();
            ElementId pipeTypeId = mainPipe.PipeType.Id;
            int junctionRuleCount = mainPipe.PipeType.RoutingPreferenceManager == null
                ? 0
                : mainPipe.PipeType.RoutingPreferenceManager.GetNumberOfRules(
                    RoutingPreferenceRuleGroupType.Junctions);
            ElementId systemTypeId = mainPipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId();
            PipingSystemType systemType = systemTypeId == null ? null : doc.GetElement(systemTypeId) as PipingSystemType;
            MEPSystemClassification expected = expectedClassificationOverride
                ?? (fcuConn.PipeSystemType == PipeSystemType.SupplyHydronic
                    ? MEPSystemClassification.SupplyHydronic : MEPSystemClassification.ReturnHydronic);
            if (systemType == null || systemType.SystemClassification != expected)
                throw new InvalidOperationException($"{circuit}主管（元素 ID: {mainPipe.Id.IntegerValue}）系统分类不匹配："
                    + $"期望 {expected}，实际 {systemType?.SystemClassification.ToString() ?? "未指定"}。请重新选择正确系统的主管。");
            XYZ startPt = fcuConn.Origin;
            XYZ connDir = fcuConn.CoordinateSystem.BasisZ;

            // 1. 水平阀门平直段
            XYZ p1 = startPt + connDir * valveClearance;

            // 2. 向下翻弯避让高差
            XYZ p2 = p1 - XYZ.BasisZ * flipDrop;

            // 3. 计算在主管轴线上的正交投影点
            LocationCurve mainLocCurve = mainPipe.Location as LocationCurve;
            if (mainLocCurve == null)
            {
                result.ErrorMessage = "选中的主管缺少有效中轴线(LocationCurve)。";
                return result;
            }

            Curve mainCurve = mainLocCurve.Curve;
            if (!(mainCurve is Line) || !mainCurve.IsBound
                || Math.Abs(mainCurve.GetEndPoint(0).Z - mainCurve.GetEndPoint(1).Z) > MM_TO_FEET)
                throw new InvalidOperationException("当前 PoC 只支持水平直线主管。");
            if (Math.Abs(connDir.Z) > 1e-6 || fcuConn.IsConnected)
                throw new InvalidOperationException("FCU 水管接口必须水平朝外且未被占用。");
            IntersectionResult projectRes = mainCurve.Project(p2);
            if (projectRes == null)
            {
                result.ErrorMessage = "支管投影超出主管轴线有效范围。";
                return result;
            }

            XYZ pMainBreak = projectRes.XYZPoint;
            XYZ p3 = new XYZ(pMainBreak.X, pMainBreak.Y, p2.Z);
            XYZ p4 = pMainBreak;

            double normalized = mainCurve.ComputeNormalizedParameter(projectRes.Parameter);
            double minLength = Math.Max(doc.Application.ShortCurveTolerance, MM_TO_FEET);
            XYZ[] points = { startPt, p1, p2, p3, p4 };
            if (normalized <= 0 || normalized >= 1
                || p4.DistanceTo(mainCurve.GetEndPoint(0)) <= minLength
                || p4.DistanceTo(mainCurve.GetEndPoint(1)) <= minLength)
            {
                double lengthMm = mainCurve.Length * FEET_TO_MM;
                double alongMm = normalized * lengthMm;
                double distanceToEndMm = Math.Min(p4.DistanceTo(mainCurve.GetEndPoint(0)),
                    p4.DistanceTo(mainCurve.GetEndPoint(1))) * FEET_TO_MM;
                string reason = normalized < 0
                    ? $"投影越过管段起点 {-alongMm:F1} mm"
                    : normalized > 1
                        ? $"投影越过管段终点 {alongMm - lengthMm:F1} mm"
                        : $"投影距最近端点仅 {distanceToEndMm:F1} mm，几何检查要求大于 {minLength * FEET_TO_MM:F1} mm";
                throw new InvalidOperationException(
                    $"{circuit}支管不能接入所选主管（元素 ID: {mainPipe.Id.IntegerValue}，系统: {systemType.Name}）。"
                    + Environment.NewLine + reason + $"；所选管段总长 {lengthMm:F1} mm。"
                    + Environment.NewLine + "当前固定路线只计算到所选单段主管的垂直投影，不会沿管网寻找其他接入点。"
                    + "请选取覆盖该投影位置的同系统管段，或调整设备位置/布管路线；不会自动延长主管或把接入点挪到端头。");
            }
            for (int i = 1; i < points.Length; i++)
                if (points[i - 1].DistanceTo(points[i]) <= minLength)
                    throw new InvalidOperationException("固定路径产生零长度或过短管段，请调整安装高度、下翻高度或主管位置。");

            ElementId levelId = (mainPipe.ReferenceLevel != null) ? mainPipe.ReferenceLevel.Id : ElementId.InvalidElementId;

            // 创建 4 段支管
            Pipe pipeSeg1 = Pipe.Create(doc, systemTypeId, pipeTypeId, levelId, startPt, p1);
            Pipe pipeSeg2 = Pipe.Create(doc, systemTypeId, pipeTypeId, levelId, p1, p2);
            Pipe pipeSeg3 = Pipe.Create(doc, systemTypeId, pipeTypeId, levelId, p2, p3);
            Pipe pipeSeg4 = Pipe.Create(doc, systemTypeId, pipeTypeId, levelId, p3, p4);

            pipeSeg1.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).Set(branchDia);
            pipeSeg2.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).Set(branchDia);
            pipeSeg3.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).Set(branchDia);
            pipeSeg4.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).Set(branchDia);

            doc.Regenerate();

            Pipe[] segments = { pipeSeg1, pipeSeg2, pipeSeg3, pipeSeg4 };
            int[] startIds = new int[4];
            int[] endIds = new int[4];
            string[] roles = { "设备水平预留段", "下翻段", "横向接近主管段", "竖直接入主管段" };
            for (int i = 0; i < segments.Length; i++)
            {
                // 在任何管件改变管长之前锁定端头身份，后续不能重新按最近距离选端头。
                startIds[i] = PipeConnectorAccess.GetClosestConnector(segments[i], points[i], MM_TO_FEET).Id;
                endIds[i] = PipeConnectorAccess.GetClosestConnector(segments[i], points[i + 1], MM_TO_FEET).Id;
                failureReporter.ElementRoles[segments[i].Id.IntegerValue] = $"{circuit}第 {i + 1} 段（{roles[i]}）";
            }
            Action<string> validateGeometry = stage =>
            {
                doc.Regenerate();
                for (int i = 0; i < segments.Length; i++)
                {
                    XYZ actual = PipeConnectorAccess.GetPipeConnector(segments[i], endIds[i]).Origin
                        - PipeConnectorAccess.GetPipeConnector(segments[i], startIds[i]).Origin;
                    XYZ planned = points[i + 1] - points[i];
                    double remaining = actual.DotProduct(planned.Normalize());
                    string role = failureReporter.ElementRoles[segments[i].Id.IntegerValue];
                    if (remaining <= minLength)
                        throw new InvalidOperationException(
                            $"{stage}后，{role}被压缩至过短或反向。元素 ID: {segments[i].Id.IntegerValue}；"
                            + $"原规划长度 {planned.GetLength() * FEET_TO_MM:F1} mm，沿原方向剩余 {remaining * FEET_TO_MM:F1} mm。"
                            + "当前路径不能容纳所用管件，请调整路径尺寸或管件配置；本次连接不予提交。");
                }
            };

            // 生成直角弯头
            FamilyInstance elbow1 = ConnectPipesWithElbow(doc, PipeConnectorAccess.GetPipeConnector(pipeSeg1, endIds[0]), PipeConnectorAccess.GetPipeConnector(pipeSeg2, startIds[1]));
            validateGeometry("第一个弯头生成");
            FamilyInstance elbow2 = ConnectPipesWithElbow(doc, PipeConnectorAccess.GetPipeConnector(pipeSeg2, endIds[1]), PipeConnectorAccess.GetPipeConnector(pipeSeg3, startIds[2]));
            validateGeometry("第二个弯头生成");
            FamilyInstance elbow3 = ConnectPipesWithElbow(doc, PipeConnectorAccess.GetPipeConnector(pipeSeg3, endIds[2]), PipeConnectorAccess.GetPipeConnector(pipeSeg4, startIds[3]));
            validateGeometry("第三个弯头生成");

            // 连接风盘接管口
            Connector p1Conn = PipeConnectorAccess.GetPipeConnector(pipeSeg1, startIds[0]);
            if (p1Conn != null && !p1Conn.IsConnected && !fcuConn.IsConnected)
            {
                fcuConn.ConnectTo(p1Conn);
            }
            validateGeometry("FCU 接口连接");
            result.BranchCreated = true;
            result.FcuConnectorId = fcuConn.Id;
            result.Chain.AddRange(new[] { fcuConn.Owner.Id, pipeSeg1.Id, elbow1.Id,
                pipeSeg2.Id, elbow2.Id, pipeSeg3.Id, elbow3.Id, pipeSeg4.Id });
            if (!verifier.VerifyConnectionChain(doc, result))
                throw new InvalidOperationException("FCU 与支管、弯头之间存在未连接节点，已取消本次操作。");

            // 若用户未勾选三通切断，支管保持物理就位
            if (!enableTeeFitting)
            {
                result.TeeCreated = false;
                result.ErrorMessage = "用户配置未启用主管切断并网。";
                return result;
            }

            // =========================================================================
            // 【核心防线：SubTransaction 保护与 IsConnected 真实性校验】
            // =========================================================================
            using (SubTransaction subTee = new SubTransaction(doc))
            {
                subTee.Start();
                try
                {
                    // 1. 打断主管
                    ElementId newPipeId = PlumbingUtils.BreakCurve(doc, mainPipe.Id, pMainBreak);
                    Pipe mainPipePart2 = doc.GetElement(newPipeId) as Pipe;
                    if (mainPipePart2 != null)
                        failureReporter.ElementRoles[mainPipePart2.Id.IntegerValue] = circuit + "主管打断后第二段";

                    doc.Regenerate();

                    // 2. 抓取断点处的 3 个端头连接件
                    Connector cMain1 = PipeConnectorAccess.GetClosestConnector(mainPipe, pMainBreak, 0.5);
                    Connector cMain2 = PipeConnectorAccess.GetClosestConnector(mainPipePart2, pMainBreak, 0.5);
                    Connector cBranch = PipeConnectorAccess.GetPipeConnector(pipeSeg4, endIds[3]);

                    if (cMain1 != null && cMain2 != null && cBranch != null)
                    {
                        HashSet<int> beforeTee = new HashSet<int>(new FilteredElementCollector(doc)
                            .OfCategory(BuiltInCategory.OST_PipeFitting).WhereElementIsNotElementType()
                            .ToElementIds().Select(id => id.IntegerValue));
                        FamilyInstance tee = doc.Create.NewTeeFitting(cMain1, cMain2, cBranch);
                        validateGeometry("三通生成");

                        if (tee == null)
                            throw new InvalidOperationException("Revit 未生成三通实例。");
                        ElementId branchAdapter = CaptureTeeAdapter(cBranch, tee, beforeTee,
                            failureReporter, circuit + "支管至三通过渡管件");
                        if (branchAdapter != null) result.Chain.Add(branchAdapter);
                        result.Chain.Add(tee.Id);
                        result.MainPart1Id = mainPipe.Id;
                        result.MainPart2Id = mainPipePart2.Id;
                        result.MainPart1AdapterId = CaptureTeeAdapter(cMain1, tee, beforeTee,
                            failureReporter, circuit + "主管第一段至三通过渡管件");
                        result.MainPart2AdapterId = CaptureTeeAdapter(cMain2, tee, beforeTee,
                            failureReporter, circuit + "主管第二段至三通过渡管件");
                        result.TeeCreated = true;
                        if (!verifier.VerifyConnectionChain(doc, result))
                            throw new InvalidOperationException("三通与两段主管或上游支管未全部连通。");
                        if (subTee.Commit() != TransactionStatus.Committed)
                            throw new InvalidOperationException("三通子事务未成功提交。");
                    }
                    else
                    {
                        subTee.RollBack();
                        result.TeeCreated = false;
                        result.ErrorMessage = "未能抓取到打断交点处的 3 个配对连接件，已安全回滚打断。";
                    }
                }
                catch (Autodesk.Revit.Exceptions.RegenerationFailedException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 捕获异常立即回滚打断，杜绝留下割裂主管
                    if (subTee.GetStatus() == TransactionStatus.Started)
                        subTee.RollBack();
                    if (result.TeeCreated)
                        result.Chain.RemoveAt(result.Chain.Count - 1);
                    result.TeeCreated = false;
                    Parameter mainDiameter = mainPipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                    string mainDn = mainDiameter == null || !mainDiameter.HasValue
                        ? "未知主管管径"
                        : $"DN{mainDiameter.AsDouble() * FEET_TO_MM:F0}";
                    result.ErrorMessage = $"{circuit}三通创建失败（管型“{mainPipe.PipeType.Name}”，主管 {mainDn}，"
                        + $"支管 DN{branchDia * FEET_TO_MM:F0}，已配置三通规则 {junctionRuleCount} 条；"
                        + "请检查同径或异径三通的 Routing Preferences）：" + ex.Message;
                }
            }

            return result;
        }

        private static ElementId CaptureTeeAdapter(Connector pipeEnd, FamilyInstance tee,
            HashSet<int> existing, RollBackOnErrorPreprocessor reporter, string role)
        {
            List<Connector> teeEnds = tee.MEPModel.ConnectorManager.Connectors.Cast<Connector>()
                .Where(c => c.Domain == Domain.DomainPiping && c.ConnectorType == ConnectorType.End).ToList();
            if (teeEnds.Any(c => pipeEnd.IsConnectedTo(c))) return null;
            List<FamilyInstance> adapters = teeEnds.Select(c => FindNewAdapter(pipeEnd, c, existing))
                .Where(f => f != null).GroupBy(f => f.Id.IntegerValue).Select(g => g.First()).ToList();
            if (adapters.Count != 1)
                throw new InvalidOperationException($"{role}识别失败：管道 {pipeEnd.Owner.Id.IntegerValue} 接口 {pipeEnd.Id}"
                    + $" 与三通 {tee.Id.IntegerValue} 未直接连通，也未经过本次生成的唯一两端过渡管件。");
            reporter.ElementRoles[adapters[0].Id.IntegerValue] = role;
            return adapters[0].Id;
        }

        private static FamilyInstance FindNewAdapter(Connector source, Connector target, HashSet<int> existing)
        {
            List<FamilyInstance> candidates = source.AllRefs.Cast<Connector>()
                .Where(c => c.Domain == Domain.DomainPiping && c.ConnectorType == ConnectorType.End
                    && source.IsConnectedTo(c))
                .Select(c => c.Owner).OfType<FamilyInstance>()
                .Where(f => f.Category != null && f.Category.Id.IntegerValue == (int)BuiltInCategory.OST_PipeFitting
                    && !existing.Contains(f.Id.IntegerValue))
                .GroupBy(f => f.Id.IntegerValue).Select(g => g.First())
                .Where(f =>
                {
                    List<Connector> ends = f.MEPModel.ConnectorManager.Connectors.Cast<Connector>()
                        .Where(c => c.Domain == Domain.DomainPiping && c.ConnectorType == ConnectorType.End).ToList();
                    return ends.Count == 2 && ends.All(c => c.IsConnected)
                        && ends.Any(c => c.IsConnectedTo(source)) && ends.Any(c => c.IsConnectedTo(target));
                }).ToList();
            return candidates.Count == 1 ? candidates[0] : null;
        }

        private FamilyInstance ConnectPipesWithElbow(Document doc, Connector c1, Connector c2)
        {
            if (c1.IsConnected || c2.IsConnected || c1.Origin.DistanceTo(c2.Origin) > MM_TO_FEET)
                throw new InvalidOperationException("预定弯头接口已被占用或端点未重合，无法创建弯头。");
            FamilyInstance elbow = doc.Create.NewElbowFitting(c1, c2);
            if (elbow == null) throw new InvalidOperationException("Revit 未生成弯头，不能继续报告支管连通。");
            return elbow;
        }
    }
}
