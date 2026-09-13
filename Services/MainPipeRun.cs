using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Media3D;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using static FCUAutoDesign.RevitUnits;

namespace FCUAutoDesign
{
    internal class MainPipeRun
    {
        private readonly HashSet<ElementId> segments = new HashSet<ElementId>();
        public MainPipeRun(Pipe initial) { segments.Add(initial.Id); }
        public Pipe AnySegment(Document doc)
        {
            return segments.Select(id => doc.GetElement(id) as Pipe).FirstOrDefault(p => p != null)
                ?? throw new InvalidOperationException("所选主管的已登记管段全部不存在。");
        }
        public Pipe Resolve(Document doc, XYZ approach)
        {
            double tolerance = Math.Max(doc.Application.ShortCurveTolerance, MM_TO_FEET);
            List<Pipe> pipes = new List<Pipe>();
            List<Point3D> starts = new List<Point3D>(), ends = new List<Point3D>();
            foreach (ElementId id in segments)
            {
                Pipe pipe = doc.GetElement(id) as Pipe;
                Line line = (pipe?.Location as LocationCurve)?.Curve as Line;
                if (line == null || !line.IsBound) continue;
                pipes.Add(pipe);
                XYZ a = line.GetEndPoint(0), b = line.GetEndPoint(1);
                starts.Add(new Point3D(a.X, a.Y, a.Z)); ends.Add(new Point3D(b.X, b.Y, b.Z));
            }
            int match = MainPipeSegmentLocator.Locate(starts.ToArray(), ends.ToArray(),
                new Point3D(approach.X, approach.Y, approach.Z), tolerance);
            return pipes[match];
        }

        public void Register(TeeConnectionResult connection)
        {
            if (connection == null || !connection.TeeCreated) return;
            segments.Add(connection.MainPart1Id);
            segments.Add(connection.MainPart2Id);
        }
        public void VerifyPrevious(Document doc, TeeConnectionResult prior, TeeConnectionResult current)
        {
            if (prior == null || !prior.BranchCreated) return;
            if (!prior.TeeCreated)
            {
                if (!new ConnectionChainVerifier().VerifyConnectionChain(doc, prior))
                    throw new InvalidOperationException("先前房间的支管连接受到了影响。");
                return;
            }
            HashSet<ElementId> allowed = new HashSet<ElementId>(segments);
            if (current != null && current.TeeCreated)
            {
                allowed.Add(current.MainPart1Id); allowed.Add(current.MainPart2Id);
            }
            // 后续打断可能改变三通邻接主管的 ID。仅接受同一已选主管血缘内的相邻管段。
            ElementId main1 = AdjacentMain(doc, prior, prior.MainPart1AdapterId, allowed, null);
            ElementId main2 = AdjacentMain(doc, prior, prior.MainPart2AdapterId, allowed, main1);
            TeeConnectionResult live = new TeeConnectionResult
            {
                BranchCreated = true, TeeCreated = true, FcuConnectorId = prior.FcuConnectorId,
                MainPart1Id = main1, MainPart2Id = main2,
                MainPart1AdapterId = prior.MainPart1AdapterId, MainPart2AdapterId = prior.MainPart2AdapterId
            };
            live.Chain.AddRange(prior.Chain);
            string failure;
            if (!new ConnectionChainVerifier().VerifyConnectionChain(doc, live, out failure))
                throw new InvalidOperationException("先前房间连接复核失败：" + failure);
        }
        private static ElementId AdjacentMain(Document doc, TeeConnectionResult prior, ElementId adapter,
            HashSet<ElementId> allowed, ElementId exclude)
        {
            FamilyInstance node = doc.GetElement(adapter ?? prior.Chain.Last()) as FamilyInstance;
            var ids = node?.MEPModel?.ConnectorManager?.Connectors.Cast<Connector>()
                .Where(c => c.Domain == Domain.DomainPiping && c.ConnectorType == ConnectorType.End)
                .SelectMany(c => c.AllRefs.Cast<Connector>().Where(r => r.Domain == Domain.DomainPiping
                    && r.ConnectorType == ConnectorType.End && c.IsConnectedTo(r)))
                .Select(c => c.Owner.Id).Where(id => allowed.Contains(id) && id != exclude).Distinct().ToList();
            if (ids == null || ids.Count == 0 || (adapter != null && ids.Count != 1))
                throw new InvalidOperationException("先前房间的三通失去与共用主管的连接。");
            return ids.First();
        }
    }
}
