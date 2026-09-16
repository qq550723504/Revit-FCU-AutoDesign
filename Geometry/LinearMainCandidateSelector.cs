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

        public static double? MaximumPerpendicularDistance(IEnumerable<LinearMainCandidate> segments,
            IEnumerable<Point3D> approaches, double endpointTolerance)
        {
            List<LinearMainCandidate> values = segments.Where(x => x != null).ToList();
            List<Point3D> points = approaches.ToList();
            if (values.Count == 0 || points.Count == 0) return null;
            double maximum = 0;
            foreach (Point3D point in points)
            {
                List<double> distances = values.Select(candidate => PerpendicularDistance(
                    candidate, point, endpointTolerance)).Where(x => x.HasValue)
                    .Select(x => x.Value).ToList();
                if (distances.Count == 0) return null;
                maximum = Math.Max(maximum, distances.Min());
            }
            return maximum;
        }

        private static bool Covers(LinearMainCandidate candidate, Point3D point, double endpointTolerance)
        {
            return PerpendicularDistance(candidate, point, endpointTolerance).HasValue;
        }

        private static double? PerpendicularDistance(LinearMainCandidate candidate, Point3D point,
            double endpointTolerance)
        {
            Vector3D axis = candidate.End - candidate.Start;
            double lengthSquared = axis.X * axis.X + axis.Y * axis.Y;
            if (lengthSquared <= endpointTolerance * endpointTolerance) return null;
            double length = Math.Sqrt(lengthSquared);
            double t = ((point.X - candidate.Start.X) * axis.X
                + (point.Y - candidate.Start.Y) * axis.Y) / lengthSquared;
            if (t * length <= endpointTolerance || (1 - t) * length <= endpointTolerance) return null;
            double projectionX = candidate.Start.X + t * axis.X;
            double projectionY = candidate.Start.Y + t * axis.Y;
            double offsetX = point.X - projectionX;
            double offsetY = point.Y - projectionY;
            return Math.Sqrt(offsetX * offsetX + offsetY * offsetY);
        }
    }
}
