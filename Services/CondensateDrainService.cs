using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Media3D;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using static FCUAutoDesign.RevitUnits;

namespace FCUAutoDesign
{
    internal class CondensateDrainService
    {
        private readonly ConnectionChainVerifier verifier = new ConnectionChainVerifier();

        // 支管、弯头、主管打断和三通属于同一子事务；失败时不保留局部残管。
        public CondensateDrainResult ConnectToMain(Document doc, Connector drainConn, Pipe mainPipe,
            ElementId levelId, double dia,
            RollBackOnErrorPreprocessor failureReporter, double leadLength, double bendOffset)
        {
            using (SubTransaction transaction = new SubTransaction(doc))
            {
                try
                {
                    if (mainPipe == null) throw new InvalidOperationException("未选择冷凝水主管。");
                    PipeType pipeType = mainPipe.PipeType;
                    if (pipeType == null) throw new InvalidOperationException("所选冷凝水主管没有有效管型。");
                    ElementId systemId = mainPipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId();
                    PipingSystemType system = systemId == null ? null : doc.GetElement(systemId) as PipingSystemType;
                    PipeSystemType? expected = system == null ? null
                        : CondensateSystemPolicy.ConnectorTypeFor(system.SystemClassification);
                    if (!expected.HasValue)
                        throw new InvalidOperationException("冷凝水主管必须属于 卫生设备（Sanitary）分类。");
                    if (levelId == null || !(doc.GetElement(levelId) is Level))
                        throw new InvalidOperationException("目标房间没有有效的基准标高。");
                    if (double.IsNaN(dia) || double.IsInfinity(dia) || dia <= 0)
                        throw new InvalidOperationException("冷凝水管径必须为有限正数。");
                    if (drainConn == null || drainConn.Domain != Domain.DomainPiping
                        || drainConn.ConnectorType != ConnectorType.End
                        || !CondensateSystemPolicy.IsCandidate(drainConn.PipeSystemType, expected.Value) || drainConn.IsConnected)
                        throw new InvalidOperationException($"设备必须具有未占用的 卫生设备（{expected.Value}）管道端接口，不能使用风管接口。");
                    Line mainLine = (mainPipe.Location as LocationCurve)?.Curve as Line;
                    if (mainLine == null || !mainLine.IsBound)
                        throw new InvalidOperationException("当前冷凝水接入只支持有界直线主管。");
                    XYZ start = drainConn.Origin;
                    XYZ direction = drainConn.CoordinateSystem.BasisZ;
                    double minLength = Math.Max(doc.Application.ShortCurveTolerance, MM_TO_FEET);
                    Point3D[] plan = CondensateRoutePlanner.Plan(Point(start),
                        new Vector3D(direction.X, direction.Y, direction.Z),
                        Point(mainLine.GetEndPoint(0)), Point(mainLine.GetEndPoint(1)),
                        leadLength, minLength, bendOffset);
                    XYZ[] points = plan.Select(p => new XYZ(p.X, p.Y, p.Z)).ToArray();
                    XYZ join = points[points.Length - 1];
                    CondensateDrainResult result = new CondensateDrainResult { MinLength = minLength };
                    result.Connection.FcuConnectorId = drainConn.Id;
                    result.Connection.Chain.Add(drainConn.Owner.Id);
                    failureReporter.ElementRoles[mainPipe.Id.IntegerValue] = "冷凝水主管";

                    if (transaction.Start() != TransactionStatus.Started)
                        throw new InvalidOperationException("冷凝水子事务未能启动。");
                    List<Pipe> pipes = new List<Pipe>();
                    for (int i = 1; i < points.Length; i++)
                    {
                        Pipe pipe = Pipe.Create(doc, system.Id, pipeType.Id, levelId, points[i - 1], points[i]);
                        Parameter diameter = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                        if (diameter == null || diameter.IsReadOnly || !diameter.Set(dia))
                            throw new InvalidOperationException("冷凝水主管的管型无法设置支管所需管径。");
                        pipes.Add(pipe);
                        result.PipeIds.Add(pipe.Id);
                        result.PlannedDirections.Add((points[i] - points[i - 1]).Normalize());
                        failureReporter.ElementRoles[pipe.Id.IntegerValue] = $"冷凝水第 {i} 段支管";
                    }
                    doc.Regenerate();
                    for (int i = 0; i < pipes.Count; i++)
                    {
                        result.StartConnectorIds.Add(EndAt(pipes[i], points[i]).Id);
                        result.EndConnectorIds.Add(EndAt(pipes[i], points[i + 1]).Id);
                    }
                    result.Connection.Chain.Add(pipes[0].Id);
                    for (int i = 1; i < pipes.Count; i++)
                    {
                        FamilyInstance elbow = doc.Create.NewElbowFitting(
                            PipeConnectorAccess.GetPipeConnector(pipes[i - 1], result.EndConnectorIds[i - 1]),
                            PipeConnectorAccess.GetPipeConnector(pipes[i], result.StartConnectorIds[i]));
                        if (elbow == null) throw new InvalidOperationException("Revit 未生成冷凝水弯头。");
                        failureReporter.ElementRoles[elbow.Id.IntegerValue] = "冷凝水弯头";
                        result.Connection.Chain.Add(elbow.Id);
                        result.Connection.Chain.Add(pipes[i].Id);
                        doc.Regenerate();
                        VerifyGeometry(doc, result);
                    }
                    HashSet<int> existingFittings = new HashSet<int>(new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_PipeFitting).WhereElementIsNotElementType()
                        .ToElementIds().Select(id => id.IntegerValue));
                    ElementId fcuId = drainConn.Owner.Id;
                    int fcuConnectorId = drainConn.Id;
                    drainConn.ConnectTo(PipeConnectorAccess.GetPipeConnector(pipes[0], result.StartConnectorIds[0]));
                    doc.Regenerate();
                    Connector source = ((FamilyInstance)doc.GetElement(fcuId)).MEPModel.ConnectorManager.Connectors
                        .Cast<Connector>().Single(c => c.Id == fcuConnectorId);
                    Connector target = PipeConnectorAccess.GetPipeConnector(pipes[0], result.StartConnectorIds[0]);
                    if (!source.IsConnectedTo(target))
                    {
                        FamilyInstance adapter = FindNewAdapter(source, target, existingFittings);
                        if (adapter == null)
                            throw new InvalidOperationException($"设备接口 {fcuId.IntegerValue}:{fcuConnectorId} 与首段管 {pipes[0].Id.IntegerValue}"
                                + "既未直接连通，也未通过本次生成的唯一两端管件连通。");
                        result.Connection.Chain.Insert(1, adapter.Id);
                        failureReporter.ElementRoles[adapter.Id.IntegerValue] = "冷凝水设备接口自动过渡管件";
                    }
                    VerifyGeometry(doc, result);

                    ElementId otherId = PlumbingUtils.BreakCurve(doc, mainPipe.Id, join);
                    Pipe other = doc.GetElement(otherId) as Pipe;
                    if (other == null) throw new InvalidOperationException("冷凝水主管打断未生成第二段。");
                    failureReporter.ElementRoles[other.Id.IntegerValue] = "冷凝水主管打断后第二段";
                    doc.Regenerate();
                    HashSet<int> beforeTee = new HashSet<int>(new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_PipeFitting).WhereElementIsNotElementType()
                        .ToElementIds().Select(id => id.IntegerValue));
                    int mainEndId = EndAt(mainPipe, join).Id;
                    int otherEndId = EndAt(other, join).Id;
                    FamilyInstance tee = doc.Create.NewTeeFitting(
                        PipeConnectorAccess.GetPipeConnector(mainPipe, mainEndId),
                        PipeConnectorAccess.GetPipeConnector(other, otherEndId),
                        PipeConnectorAccess.GetPipeConnector(pipes[pipes.Count - 1], result.EndConnectorIds[pipes.Count - 1]));
                    if (tee == null) throw new InvalidOperationException("Revit 未生成冷凝水接入三通，请检查主管管型的管件配置。");
                    failureReporter.ElementRoles[tee.Id.IntegerValue] = "冷凝水接入三通";
                    doc.Regenerate();
                    ElementId branchAdapter = CaptureTeeAdapter(
                        PipeConnectorAccess.GetPipeConnector(pipes[pipes.Count - 1], result.EndConnectorIds[pipes.Count - 1]),
                        tee, beforeTee, failureReporter, "冷凝水支管至三通过渡管件");
                    if (branchAdapter != null) result.Connection.Chain.Add(branchAdapter);
                    result.Connection.MainPart1AdapterId = CaptureTeeAdapter(
                        PipeConnectorAccess.GetPipeConnector(mainPipe, mainEndId), tee, beforeTee,
                        failureReporter, "冷凝水主管第一段至三通过渡管件");
                    result.Connection.MainPart2AdapterId = CaptureTeeAdapter(
                        PipeConnectorAccess.GetPipeConnector(other, otherEndId), tee, beforeTee,
                        failureReporter, "冷凝水主管第二段至三通过渡管件");
                    result.Connection.Chain.Add(tee.Id);
                    result.Connection.MainPart1Id = mainPipe.Id;
                    result.Connection.MainPart2Id = other.Id;
                    result.Connection.BranchCreated = true;
                    result.Connection.TeeCreated = true;
                    doc.Regenerate();
                    if (!VerifyConnection(doc, result))
                        throw new InvalidOperationException("冷凝水设备、支管、管件和两段主管未形成完整连接链。");
                    TransactionStatus status = transaction.Commit();
                    if (status != TransactionStatus.Committed)
                        throw new InvalidOperationException($"冷凝水子事务未成功提交（状态：{status}）。");
                    result.Connected = true;
                    return result;
                }
                catch (Autodesk.Revit.Exceptions.RegenerationFailedException)
                {
                    throw; // 模型再生成失败必须交由外层终止主事务。
                }
                catch (Exception ex)
                {
                    if (transaction.GetStatus() == TransactionStatus.Started
                        && transaction.RollBack() != TransactionStatus.RolledBack)
                        throw new InvalidOperationException("冷凝水回滚失败，必须取消整次操作。", ex);
                    return new CondensateDrainResult
                    {
                        ErrorMessage = $"{ex.GetType().Name}: {ex.Message}；本次冷凝水操作未保留。"
                    };
                }
            }
        }

        public bool VerifyConnection(Document doc, CondensateDrainResult result)
        {
            VerifyGeometry(doc, result);
            string failure;
            if (!verifier.VerifyConnectionChain(doc, result.Connection, out failure))
                throw new InvalidOperationException("冷凝水连接验证失败：" + failure);
            return true;
        }

        private static void VerifyGeometry(Document doc, CondensateDrainResult result)
        {
            for (int i = 0; i < result.PipeIds.Count; i++)
            {
                Pipe pipe = doc.GetElement(result.PipeIds[i]) as Pipe;
                if (pipe == null) throw new InvalidOperationException("冷凝水支管在验证时已不存在。");
                XYZ start = PipeConnectorAccess.GetPipeConnector(pipe, result.StartConnectorIds[i]).Origin;
                XYZ end = PipeConnectorAccess.GetPipeConnector(pipe, result.EndConnectorIds[i]).Origin;
                XYZ actual = end - start;
                if (actual.GetLength() <= result.MinLength
                    || actual.Normalize().DistanceTo(result.PlannedDirections[i]) > 1e-5)
                    throw new InvalidOperationException($"冷凝水第 {i + 1} 段在管件生成后过短、反向或偏离规划方向，不能提交。");
            }
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
            // 只接受当前连接操作新建、直接连接指定两端的唯一管件，不遍历已有管网绕路。
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

        private static Connector EndAt(Pipe pipe, XYZ point)
        {
            Connector connector = PipeConnectorAccess.GetClosestConnector(pipe, point, MM_TO_FEET);
            if (connector == null || connector.IsConnected)
                throw new InvalidOperationException("冷凝水预定接入点没有可用管道端接口。");
            return connector;
        }

        private static Point3D Point(XYZ point) { return new Point3D(point.X, point.Y, point.Z); }
    }
}
