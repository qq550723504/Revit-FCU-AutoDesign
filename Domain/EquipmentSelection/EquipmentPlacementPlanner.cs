using System;
using System.Collections.Generic;

namespace FCUAutoDesign.Business.EquipmentSelection
{
    /// <summary>
    /// Revit/WPF independent implementation of the customer's equal-bay rule.
    /// Adapters map the returned local points to model coordinates and connect
    /// every returned device in the same transaction.
    /// </summary>
    public sealed class EquipmentPlacementPlanner
    {
        public static int GetUnitCount(double lengthM)
        {
            if (double.IsNaN(lengthM) || double.IsInfinity(lengthM) || lengthM <= 0 || lengthM > 40)
                throw new ArgumentOutOfRangeException("lengthM", "台数规则仅支持 0 < L <= 40m。");
            return (int)Math.Ceiling(lengthM / 5.0);
        }

        public IList<EquipmentPlacementPoint> Build(
            double lengthM, double widthM, double baseElevationM,
            double installationHeightM, double placementOffsetM, int unitCount)
        {
            if (double.IsNaN(lengthM) || double.IsInfinity(lengthM) || lengthM <= 0)
                throw new ArgumentOutOfRangeException("lengthM");
            if (double.IsNaN(widthM) || double.IsInfinity(widthM) || widthM <= 0)
                throw new ArgumentOutOfRangeException("widthM");
            if (double.IsNaN(placementOffsetM) || double.IsInfinity(placementOffsetM)
                || placementOffsetM < 0 || placementOffsetM >= widthM)
                throw new ArgumentOutOfRangeException("placementOffsetM");
            if (unitCount < 1)
                throw new ArgumentOutOfRangeException("unitCount");
            if (double.IsNaN(baseElevationM) || double.IsInfinity(baseElevationM)
                || double.IsNaN(installationHeightM) || double.IsInfinity(installationHeightM)
                || double.IsInfinity(baseElevationM + installationHeightM))
                throw new ArgumentOutOfRangeException("baseElevationM");

            List<EquipmentPlacementPoint> points = new List<EquipmentPlacementPoint>(unitCount);
            for (int i = 1; i <= unitCount; i++)
            {
                points.Add(new EquipmentPlacementPoint
                {
                    Sequence = i,
                    XAlongLengthM = lengthM * (2 * i - 1) / (2.0 * unitCount),
                    YFromReferenceEdgeM = placementOffsetM,
                    AbsoluteElevationM = baseElevationM + installationHeightM
                });
            }
            return points;
        }
    }
}
