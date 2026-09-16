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
        public string NetworkId { get; }
        public MainPipeRun(Pipe initial)
        {
            if (initial == null) throw new ArgumentNullException("initial");
            segments.Add(initial.Id);
            DiscoverExistingStraightRun(initial);
            NetworkId = "main:" + initial.UniqueId;
        }
        private MainPipeRun(IEnumerable<ElementId> ids, string networkId)
        {
            segments.UnionWith(ids); NetworkId = networkId;
        }
        public MainPipeRun Fork() { return new MainPipeRun(segments, NetworkId); }
        internal IList<Pipe> ExistingSegments(Document doc)
        {
            return segments.Select(id => doc.GetElement(id) as Pipe).Where(x => x != null).ToList();
        }
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

        private void DiscoverExistingStraightRun(Pipe initial)
        {
            Document doc = initial.Document;
            Line seed = (initial.Location as LocationCurve)?.Curve as Line;
            if (seed == null || !seed.IsBound) return;
            XYZ seedDirection = seed.Direction.Normalize();
            ElementId systemTypeId = initial.get_Parameter(
                BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId() ?? ElementId.InvalidElementId;
            double tolerance = Math.Max(doc.Application.ShortCurveTolerance, MM_TO_FEET);
            Queue<Element> queue = new Queue<Element>();
            HashSet<ElementId> visited = new HashSet<ElementId>();
            queue.Enqueue(initial);
            while (queue.Count > 0)
            {
                Element owner = queue.Dequeue();
                if (owner == null || !visited.Add(owner.Id)) continue;
                foreach (Connector connector in Connectors(owner))
                foreach (Connector reference in connector.AllRefs.Cast<Connector>())
                {
                    if (reference.Domain != Domain.DomainPiping || !connector.IsConnectedTo(reference)) continue;
                    Element adjacent = reference.Owner;
                    Pipe pipe = adjacent as Pipe;
                    if (pipe != null)
                    {
                        if (!Qualifies(pipe, seed, seedDirection, systemTypeId, tolerance)) continue;
                        if (segments.Add(pipe.Id)) queue.Enqueue(pipe);
                    }
                    else if (adjacent is FamilyInstance fitting
                        && fitting.Category != null
                        && fitting.Category.Id.IntegerValue == (int)BuiltInCategory.OST_PipeFitting)
                    {
                        queue.Enqueue(fitting);
                    }
                }
            }
        }

        private static IEnumerable<Connector> Connectors(Element element)
        {
            Pipe pipe = element as Pipe;
            if (pipe != null) return pipe.ConnectorManager.Connectors.Cast<Connector>();
            FamilyInstance fitting = element as FamilyInstance;
            return fitting?.MEPModel?.ConnectorManager == null
                ? Enumerable.Empty<Connector>()
                : fitting.MEPModel.ConnectorManager.Connectors.Cast<Connector>();
        }

        private static bool Qualifies(Pipe pipe, Line seed, XYZ seedDirection,
            ElementId systemTypeId, double tolerance)
        {
            Line line = (pipe.Location as LocationCurve)?.Curve as Line;
            if (line == null || !line.IsBound || Math.Abs(line.Direction.DotProduct(seedDirection)) < 1.0 - 1e-6)
                return false;
            ElementId candidateSystem = pipe.get_Parameter(
                BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId() ?? ElementId.InvalidElementId;
            if (candidateSystem != systemTypeId) return false;
            XYZ origin = seed.GetEndPoint(0);
            return DistanceToAxis(line.GetEndPoint(0), origin, seedDirection) <= tolerance
                && DistanceToAxis(line.GetEndPoint(1), origin, seedDirection) <= tolerance;
        }

        private static double DistanceToAxis(XYZ point, XYZ origin, XYZ direction)
        {
            XYZ offset = point - origin;
            return (offset - direction * offset.DotProduct(direction)).GetLength();
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
