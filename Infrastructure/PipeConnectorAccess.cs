using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;

namespace FCUAutoDesign
{
    internal static class PipeConnectorAccess
    {
        public static Connector GetPipeConnector(Pipe pipe, int connectorId)
        {
            Connector connector = pipe.ConnectorManager.Connectors.Cast<Connector>()
                .SingleOrDefault(c => c.Id == connectorId);
            if (connector == null)
                throw new InvalidOperationException($"管道 {pipe.Id.IntegerValue} 的预定接口 {connectorId} 已不存在，不能替换为其他端头。");
            return connector;
        }

        public static Connector GetClosestConnector(Pipe pipe, XYZ point, double maxDistTolerance = 2.0)
        {
            if (pipe == null || pipe.ConnectorManager == null) return null;

            return pipe.ConnectorManager.Connectors.Cast<Connector>()
                .Where(c => c.Origin.DistanceTo(point) <= maxDistTolerance)
                .OrderBy(c => c.IsConnected ? 1 : 0)
                .ThenBy(c => c.Origin.DistanceTo(point))
                .FirstOrDefault();
        }
    }
}
