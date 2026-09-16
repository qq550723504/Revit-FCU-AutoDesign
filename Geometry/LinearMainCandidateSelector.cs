using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Media3D;

namespace FCUAutoDesign
{
    internal sealed class LinearMainCandidate
    {
        public string Id { get; set; }
        public Point3D Start { get; set; }
        public Point3D End { get; set; }
    }

    internal sealed class LinearMainCandidateResult
    {
        public string UniqueCandidateId { get; set; }
        public IList<string> EligibleCandidateIds { get; } = new List<string>();
    }

    internal static class LinearMainCandidateSelector
    {
        public static LinearMainCandidateResult Select(IList<LinearMainCandidate> candidates,
            IList<Point3D> approaches, double endpointTolerance)
        {
            if (candidates == null || approaches == null || approaches.Count == 0
                || endpointTolerance <= 0 || double.IsNaN(endpointTolerance))
                throw new InvalidOperationException("主管候选选择参数无效。");
            LinearMainCandidateResult result = new LinearMainCandidateResult();
            foreach (LinearMainCandidate candidate in candidates.Where(x => x != null)
                .OrderBy(x => x.Id, StringComparer.Ordinal))
            {
                if (CoversAll(new[] { candidate }, approaches, endpointTolerance))
                    result.EligibleCandidateIds.Add(candidate.Id);
            }
            if (result.EligibleCandidateIds.Count == 1)
                result.UniqueCandidateId = result.EligibleCandidateIds[0];
            return result;
        }

        public static bool CoversAll(IEnumerable<LinearMainCandidate> segments,
            IEnumerable<Point3D> approaches, double endpointTolerance)
        {
            List<LinearMainCandidate> values = segments.Where(x => x != null).ToList();
            return approaches.All(point => values.Any(candidate => Covers(candidate, point, endpointTolerance)));
        }

        private static bool Covers(LinearMainCandidate candidate, Point3D point, double endpointTolerance)
        {
            Vector3D axis = candidate.End - candidate.Start;
            double lengthSquared = axis.X * axis.X + axis.Y * axis.Y;
            if (lengthSquared <= endpointTolerance * endpointTolerance) return false;
            double length = Math.Sqrt(lengthSquared);
            double t = ((point.X - candidate.Start.X) * axis.X
                + (point.Y - candidate.Start.Y) * axis.Y) / lengthSquared;
            return t * length > endpointTolerance && (1 - t) * length > endpointTolerance;
        }
    }
}
