using System.Text;
using Autodesk.Revit.UI;

namespace FCUAutoDesign
{
    internal class ExecutionReportPresenter
    {
        public void ShowHonestReport(
            ExecutionOutcome outcome,
            double roomAreaSqm,
            double coolingLoadKw,
            int dn,
            bool autoSizing,
            bool enableReturn,
            bool returnPicked)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("【FCU 自动化执行核查报告】");
            sb.AppendLine($"1. 规则面积 L×H: {roomAreaSqm:F1} ㎡ | 设计冷负荷: {coolingLoadKw:F2} kW");
            sb.AppendLine($"2. 供水接口管径: DN{dn}（支管采用各回路设备实际接口尺寸；正式自动选径未启用）");
            sb.AppendLine($"3. 空间布设: {(outcome.PlacementVerified ? "【正常】标高叠加基准，室内落点校验通过" : "【警告】未通过落点校验")}");

            // 供水状态
            string supplyStatus = outcome.SupplyTeeConnected
                ? "【模型连通】FCU 接口、全部支管弯头、三通及两段主管逐段校验通过"
                : outcome.SupplyBranchCreated
                    ? "【部分完成】FCU 与支管连通，末端未接主管；主管未打断或打断已回滚"
                    : "【未完成】";
            sb.AppendLine($"4. 供水回路: {supplyStatus}");

            // 回水状态
            if (!enableReturn)
            {
                sb.AppendLine("5. 回水回路: 未启用");
            }
            else if (!returnPicked)
            {
                sb.AppendLine("5. 回水回路: 用户跳过主管拾取");
            }
            else
            {
                string retStatus = outcome.ReturnTeeConnected
                    ? "【模型连通】FCU 接口至回水主管完整连接链校验通过"
                    : outcome.ReturnBranchCreated
                        ? "【部分完成】FCU 与支管连通，末端未接主管；主管未打断或打断已回滚"
                        : "【未完成】";
                sb.AppendLine($"5. 回水回路: {retStatus}");
            }

            // 冷凝水状态
            sb.AppendLine($"6. 冷凝水管: {(outcome.CondensateConnected ? "【模型连通】按实际标高连接，未验证坡度或重力排水能力；设备至所选主管连接链验证通过" : outcome.CondensateEnabled ? "【未完成】详见运行提示" : "未启用")}");
            sb.AppendLine("本结果仅验证模型位置和连接关系；未验证水力性能、设备外形净距或碰撞避让。");

            if (outcome.Warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("【运行提示 / 警示】:");
                foreach (string w in outcome.Warnings)
                {
                    sb.AppendLine("• " + w);
                }
            }

            TaskDialog dialog = new TaskDialog("FCU 插件执行结果");
            dialog.MainInstruction = outcome.SupplyTeeConnected && enableReturn && returnPicked && outcome.ReturnTeeConnected
                ? "供回水模型连接链验证通过" : "局部执行完成，供回水连接尚未全部通过";
            dialog.MainContent = sb.ToString();
            dialog.Show();
        }
    }
}
