using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Media3D;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using static FCUAutoDesign.RevitUnits;

namespace FCUAutoDesign
{
    internal class CondensateSeparationService
    {
        private const int MaxCandidateStep = 8;
        private const int MaxModelAttempts = 12;
        private readonly HydronicConnectionService connection = new HydronicConnectionService();
        private readonly ConnectionChainVerifier verifier = new ConnectionChainVerifier();

        // 复用供回水接管；横移候选在水平预留后、下翻前横移。
        public CondensateDrainResult Connect(Document doc, Connector connector, Pipe main,
            ElementId levelId, double diameter, RollBackOnErrorPreprocessor reporter,
            TeeConnectionResult supply, TeeConnectionResult ret, MainPipeRun run = null,
            RoomBatchContext batch = null, double leadLength = 500 * MM_TO_FEET,
            double drop = 150 * MM_TO_FEET, double? minimumStraightLength = null)
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
            string firstReason = null;
            int attempts = 0;
            int screened = 0, skipped = 0;
            ElementId mainId = main.Id;
            ElementId ownerId = connector.Owner.Id;
            int connectorId = connector.Id;
            double step = Math.Max(100 * MM_TO_FEET, diameter * 4);
            int[] lateralSteps = { 0, -1, 1, -2, 2 };
            XYZ origin = connector.Origin, direction = connector.CoordinateSystem.BasisZ;
            var trace = new RoutingDiagnostic(ownerId.IntegerValue);
            Line selectedMain = (main.Location as LocationCurve)?.Curve as Line;
            if (selectedMain != null && selectedMain.IsBound)
                trace.Add("selected condensate main="+main.Id.IntegerValue+" type="+main.GetTypeId().IntegerValue
                    +" start="+RoutingDiagnostic.Point(selectedMain.GetEndPoint(0))+" end="+RoutingDiagnostic.Point(selectedMain.GetEndPoint(1)));
            FamilyInstance diagnosticOwner = doc.GetElement(ownerId) as FamilyInstance;
            foreach (Connector port in diagnosticOwner.MEPModel.ConnectorManager.Connectors)
                if (port.Domain == Domain.DomainPiping)
                    trace.Add("connector="+port.Id+" system="+port.PipeSystemType+" origin="+RoutingDiagnostic.Point(port.Origin)
                        +" direction="+port.CoordinateSystem.BasisZ+" connected="+port.IsConnected);
            var obstacles = ReadPipeObstacles(doc, supply, ret, trace).ToList();
            double minLength = Math.Max(doc.Application.ShortCurveTolerance, MM_TO_FEET);
            double[] earlyLeads = minimumStraightLength.HasValue ? OutletLeadPlanner.BeforeObstacles(
                new Point3D(origin.X, origin.Y, origin.Z), new Vector3D(direction.X, direction.Y, direction.Z),
                ReadObstacleCorners(doc, supply, ret, trace), leadLength,
                Math.Max(diameter / 2, connector.Radius), step, minLength)
                .Where(x => x > minimumStraightLength.Value).ToArray() : new double[0];
            trace.Add("earlyLeads(mm)="+string.Join(",",earlyLeads.Select(x=>(x*FEET_TO_MM).ToString("F3",System.Globalization.CultureInfo.InvariantCulture))));
            // 优先目标长度；指定净长下限后允许提前转弯，实际安装后再复核净长。
            double[] leads = new[] { leadLength }.Concat(earlyLeads).Concat(Enumerable.Range(1, MaxCandidateStep)
                .Select(i => leadLength + i * step)).ToArray();

            foreach (bool direct in new[] { true, false })
            foreach (int lateralStep in lateralSteps)
            {
                // 无下翻最多试三个长度，为下翻避让保留试建预算。
                if (direct && lateralStep != 0) continue;
                int maxDropStep = lateralStep == 0 ? MaxCandidateStep : 4;
                foreach (double candidateLead in direct ? leads.Take(3) : leads)
                {
                    for (int dropStep = 0; dropStep <= (direct ? 0 : maxDropStep); dropStep++)
                    {
                        double candidateDrop = direct ? 0 : drop + dropStep * step;
                        double candidateLateral = lateralStep * step;
                        if (attempts >= MaxModelAttempts || trace.Clock.Elapsed.TotalSeconds >= 10)
                        {
                            lastReason = "达到诊断预算，停止新试建；未穷尽所有路线。";
                            goto Finished;
                        }
                        screened++;
                        Point3D[] prefix = LowerFlipRoutePlanner.Approach(
                            new Point3D(origin.X, origin.Y, origin.Z), new Vector3D(direction.X,direction.Y,direction.Z),
                            candidateLead,candidateDrop,candidateLateral,minLength);
                        string blocked = null;
                        foreach (PipeObstacle obstacle in obstacles)
                        {
                            for (int i=1;i<prefix.Length;i++)
                                if (SegmentClearance.Distance(prefix[i-1],prefix[i],obstacle.Start,obstacle.End)
                                    < obstacle.Radius + diameter/2 - minLength)
                                { blocked="prefix segment="+i+" pipe="+obstacle.Id; break; }
                            if (blocked != null) break;
                        }
                        if (blocked != null)
                        {
                            skipped++;
                            trace.Add("PREFILTER "+blocked+" lead/drop/lateral(mm)="
                                +RoutingDiagnostic.Point(new XYZ(candidateLead,candidateDrop,candidateLateral)));
                            continue;
                        }
                        attempts++;
                        trace.Add("BUILD "+attempts+" lead/drop/lateral(mm)="+RoutingDiagnostic.Point(new XYZ(candidateLead,candidateDrop,candidateLateral)));
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
                                    candidateLateral, minimumStraightLength);
                                trace.Add("BUILD returned");
                                if (!connectionResult.BranchCreated || !connectionResult.TeeCreated)
                                    throw new InvalidOperationException(connectionResult.ErrorMessage ?? "冷凝水连接未完成。");

                                doc.Regenerate();
                                Verify(doc, connectionResult, supply, ret);
                                batch?.VerifyNew(doc, connectionResult);
                                ConnectionInstallationVerifier.Verify(doc, connectionResult);
                                trace.Add("VERIFY passed");
                                if (candidate.Commit() != TransactionStatus.Committed)
                                    throw new InvalidOperationException("冷凝水避让候选未成功提交。");

                                CondensateDrainResult result = Wrap(doc, connectionResult);
                                result.ErrorMessage = $"冷凝水接管目标距离 {candidateLead * FEET_TO_MM:F0} mm，"
                                        + $"下翻高度 {candidateDrop * FEET_TO_MM:F0} mm，设备侧横移 {candidateLateral * FEET_TO_MM:F0} mm。"
                                        + (candidateLead < leadLength ? "首段已缩短，并已校验指定的最小净直管长度。" : "");
                                result.ErrorMessage = trace.Finish(result.ErrorMessage+Environment.NewLine
                                    +$"冷凝水诊断：预筛 {screened}，跳过 {skipped}，试建 {attempts}，耗时 {trace.Clock.Elapsed.TotalSeconds:F1} 秒；候选连接检查通过。");
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
                                if (firstReason == null) firstReason = lastReason;
                                trace.Add("FAIL "+lastReason);
                                if (candidate.GetStatus() == TransactionStatus.Started
                                    && candidate.RollBack() != TransactionStatus.RolledBack)
                                    throw new InvalidOperationException("冷凝水候选回滚失败，必须取消整次操作。", ex);
                                trace.Add("ROLLBACK complete");
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

            Finished:
            if (attempts == 0 && skipped == screened && screened > 0)
                lastReason = "所有已检查候选前段均未通过中心线距离预筛，未进行 Revit 试建。";
            return new CondensateDrainResult
            {
                ErrorMessage = trace.Finish($"冷凝水诊断：预筛 {screened}，跳过 {skipped}，试建 {attempts}，耗时 {trace.Clock.Elapsed.TotalSeconds:F1} 秒；未完成，未保留本次冷凝水操作。"
                    + $"已加入 {earlyLeads.Length} 个障碍前转弯长度。首条试建原因：{firstReason ?? "未进入试建"}"
                    + Environment.NewLine + $"最后原因：{lastReason}；预筛仅检查已生成供回水直管，不能证明所有路线不可行。")
            };
        }

        private sealed class PipeObstacle
        {
            public int Id;
            public Point3D Start, End;
            public double Radius;
        }
        private static IEnumerable<PipeObstacle> ReadPipeObstacles(Document doc,
            TeeConnectionResult supply, TeeConnectionResult ret, RoutingDiagnostic trace)
        {
            foreach (ElementId id in ObstacleIds(supply,ret))
            {
                Pipe pipe = doc.GetElement(id) as Pipe;
                Line line = (pipe?.Location as LocationCurve)?.Curve as Line;
                if (line == null || !line.IsBound) continue;
                XYZ a=line.GetEndPoint(0), b=line.GetEndPoint(1);
                double radius=pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).AsDouble()/2;
                trace.Add("pipe="+id.IntegerValue+" start="+RoutingDiagnostic.Point(a)+" end="+RoutingDiagnostic.Point(b)
                    +" nominalRadius(mm)="+(radius*FEET_TO_MM).ToString("F3",System.Globalization.CultureInfo.InvariantCulture));
                yield return new PipeObstacle { Id=id.IntegerValue,Start=new Point3D(a.X,a.Y,a.Z),End=new Point3D(b.X,b.Y,b.Z),Radius=radius };
            }
        }

        private static HashSet<ElementId> ObstacleIds(TeeConnectionResult supply, TeeConnectionResult ret)
        {
            var ids = new HashSet<ElementId>();
            foreach (TeeConnectionResult circuit in new[] { supply, ret })
            {
                if (circuit == null || !circuit.BranchCreated) continue;
                foreach (ElementId id in circuit.Chain.Skip(1)) ids.Add(id); // 排除共享 FCU。
                foreach (ElementId id in new[] { circuit.MainPart1Id, circuit.MainPart2Id,
                    circuit.MainPart1AdapterId, circuit.MainPart2AdapterId })
                    if (circuit.TeeCreated && id != null) ids.Add(id);
            }
            return ids;
        }
        private static IEnumerable<Point3D[]> ReadObstacleCorners(Document doc,
            TeeConnectionResult supply, TeeConnectionResult ret, RoutingDiagnostic trace)
        {
            foreach (ElementId id in ObstacleIds(supply,ret))
            {
                BoundingBoxXYZ box = doc.GetElement(id)?.get_BoundingBox(null);
                if (box == null) continue; // 缺盒时不建议缩短；最终实体验证仍执行。
                var corners = new List<Point3D>();
                for (int x = 0; x < 2; x++)
                for (int y = 0; y < 2; y++)
                for (int z = 0; z < 2; z++)
                {
                    XYZ p = box.Transform.OfPoint(new XYZ(x == 0 ? box.Min.X : box.Max.X,
                        y == 0 ? box.Min.Y : box.Max.Y, z == 0 ? box.Min.Z : box.Max.Z));
                    corners.Add(new Point3D(p.X, p.Y, p.Z));
                    trace.Add("box="+id.IntegerValue+" corner="+RoutingDiagnostic.Point(p));
                }
                yield return corners.ToArray();
            }
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
