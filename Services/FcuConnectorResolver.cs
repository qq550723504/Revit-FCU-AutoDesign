using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace FCUAutoDesign
{
    internal class FcuConnectorResolver
    {
        public FCUConnectors DetectFCUConnectors(FamilyInstance fcu,
            PipeSystemType condensateType = PipeSystemType.Sanitary)
        {
            FCUConnectors result = new FCUConnectors();
            var mepModel = fcu.MEPModel;
            if (mepModel == null || mepModel.ConnectorManager == null) return result;

            List<Connector> pipingConns = mepModel.ConnectorManager.Connectors
                .Cast<Connector>()
                .Where(c => c.Domain == Domain.DomainPiping && c.ConnectorType == ConnectorType.End)
                .ToList();

            // PoC 只接受唯一、明确的系统分类，不按名称或接口高度猜测供回水。
            foreach (Connector c in pipingConns)
            {
                if (c.PipeSystemType == PipeSystemType.SupplyHydronic)
                {
                    if (result.SupplyConnector != null) throw new InvalidOperationException("FCU 存在多个供水接口，当前 PoC 无法唯一确定接管口。");
                    result.SupplyConnector = c;
                }
                else if (c.PipeSystemType == PipeSystemType.ReturnHydronic)
                {
                    if (result.ReturnConnector != null) throw new InvalidOperationException("FCU 存在多个回水接口，当前 PoC 无法唯一确定接管口。");
                    result.ReturnConnector = c;
                }
                else if (CondensateSystemPolicy.IsCandidate(c.PipeSystemType, condensateType))
                {
                    if (result.CondensateConnector != null) throw new InvalidOperationException($"FCU 存在多个 {condensateType} 冷凝水候选管道端接口，无法唯一确定接管口。");
                    result.CondensateConnector = c;
                }
            }

            return result;
        }

        public string DescribeConnectors(FamilyInstance fcu)
        {
            ConnectorManager manager = fcu?.MEPModel?.ConnectorManager;
            if (manager == null) return "设备没有 MEP 接口管理器。";
            return string.Join("；", manager.Connectors.Cast<Connector>().Select(c =>
                $"接口 {c.Id}: {c.Domain}/{c.ConnectorType}/"
                + (c.Domain == Domain.DomainPiping ? c.PipeSystemType.ToString()
                    : c.Domain == Domain.DomainHvac ? c.DuctSystemType.ToString() : "非水管接口")));
        }

        public Connector GetSupplyAirOutlet(FamilyInstance fcu)
        {
            List<Connector> outlets = fcu?.MEPModel?.ConnectorManager?.Connectors
                .Cast<Connector>().Where(c => c.Domain == Domain.DomainHvac
                    && c.ConnectorType == ConnectorType.End
                    && c.DuctSystemType == DuctSystemType.SupplyAir).ToList();
            if (outlets == null || outlets.Count != 1)
                throw new InvalidOperationException("FCU 必须具有唯一的送风（SupplyAir）风管接口，才能确定真实出风方向。请检查族接口分类。");
            return outlets[0];
        }
    }
}
