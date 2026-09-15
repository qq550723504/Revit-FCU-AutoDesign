using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;

namespace FCUAutoDesign
{
    internal class ConnectionChainVerifier
    {
        // 仅沿指定路径验证，不绕经设备其他接口或已有网络。
        public bool VerifyConnectionChain(Document doc, TeeConnectionResult result)
        {
            string failure;
            return VerifyConnectionChain(doc, result, out failure);
        }

        public bool VerifyConnectionChain(Document doc, TeeConnectionResult result, out string failure)
        {
            failure = null;
            if (!result.BranchCreated)
            {
                if (result.Chain.Count == 0) return true;
                failure = "未创建支管但连接链非空。";
                return false;
            }
            List<List<Connector>> nodes = result.Chain
                .Select(id => GetPipingConnectors(doc.GetElement(id))).ToList();
            if (nodes.Count < 2) { failure = "连接链少于两个节点。"; return false; }
            nodes[0] = nodes[0].Where(c => c.Id == result.FcuConnectorId).ToList();
            if (nodes[0].Count != 1) { failure = $"设备 {result.Chain[0].IntegerValue} 的指定管道端接口 {result.FcuConnectorId} 不存在。"; return false; }
            for (int i = 1; i < nodes.Count; i++)
            {
                bool last = i == nodes.Count - 1;
                int expectedCount = last && result.TeeCreated ? 3 : 2;
                int expectedConnected = last && !result.TeeCreated ? 1 : expectedCount;
                if (!nodes[i - 1].Any(a => nodes[i].Any(b => a.IsConnectedTo(b))))
                {
                    failure = $"连接链断点：元素 {result.Chain[i - 1].IntegerValue} → {result.Chain[i].IntegerValue} 未直接连通。"
                        + "上游接口：" + Describe(nodes[i - 1]) + "；下游接口：" + Describe(nodes[i]);
                    return false;
                }
                if (nodes[i].Count != expectedCount || nodes[i].Count(c => c.IsConnected) != expectedConnected)
                {
                    failure = $"元素 {result.Chain[i].IntegerValue} 应有 {expectedCount} 个管道端接口、{expectedConnected} 个已连接接口，"
                        + $"实际为 {nodes[i].Count}/{nodes[i].Count(c => c.IsConnected)}。" + Describe(nodes[i]);
                    return false;
                }
            }
            if (!result.TeeCreated) return true;
            List<Connector> tee = nodes[nodes.Count - 1];
            List<Connector> main1 = GetPipingConnectors(doc.GetElement(result.MainPart1Id));
            List<Connector> main2 = GetPipingConnectors(doc.GetElement(result.MainPart2Id));
            if (!VerifyMainLeg(doc, tee, main1, result.MainPart1AdapterId))
                failure = $"三通 {result.Chain.Last().IntegerValue} 未连到主管第一段 {result.MainPart1Id.IntegerValue}。";
            else if (!VerifyMainLeg(doc, tee, main2, result.MainPart2AdapterId))
                failure = $"三通 {result.Chain.Last().IntegerValue} 未连到主管第二段 {result.MainPart2Id.IntegerValue}。";
            return failure == null;
        }

        private bool VerifyMainLeg(Document doc, List<Connector> tee, List<Connector> main, ElementId adapterId)
        {
            if (adapterId == null) return tee.Any(a => main.Any(b => a.IsConnectedTo(b)));
            List<Connector> adapter = GetPipingConnectors(doc.GetElement(adapterId));
            if (adapter.Count != 2 || adapter.Any(c => !c.IsConnected)) return false;
            // 两个不同的端口必须分别连向三通和指定主管，不接受旁路网络。
            return (tee.Any(c => c.IsConnectedTo(adapter[0])) && main.Any(c => c.IsConnectedTo(adapter[1])))
                || (tee.Any(c => c.IsConnectedTo(adapter[1])) && main.Any(c => c.IsConnectedTo(adapter[0])));
        }

        private static string Describe(List<Connector> connectors)
        {
            return string.Join("；", connectors.Select(c => $"接口 {c.Id} 已连接={c.IsConnected}，相邻元素="
                + string.Join(",", c.AllRefs.Cast<Connector>()
                    .Where(r => r.Domain == Domain.DomainPiping && r.ConnectorType == ConnectorType.End
                        && c.IsConnectedTo(r)).Select(r => r.Owner.Id.IntegerValue).Distinct())));
        }

        private List<Connector> GetPipingConnectors(Element element)
        {
            ConnectorManager manager = (element as Pipe)?.ConnectorManager
                ?? (element as FamilyInstance)?.MEPModel?.ConnectorManager;
            return manager == null ? new List<Connector>() : manager.Connectors.Cast<Connector>()
                .Where(c => c.Domain == Domain.DomainPiping && c.ConnectorType == ConnectorType.End).ToList();
        }
    }
}
