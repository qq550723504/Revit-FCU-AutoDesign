using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.UI;

namespace FCUAutoDesign
{
    internal class BatchReportPresenter
    {
        public void Show(IList<RoomExecutionResult> results, FcuDesignOptions options)
        {
            StringBuilder summary = new StringBuilder();
            StringBuilder details = new StringBuilder();
            foreach (RoomExecutionResult room in results)
            {
                string status = room.Status(options);
                summary.AppendLine(room.RoomLabel + "：" + status);
                details.AppendLine(room.RoomLabel + "：" + status);
                if (room.Design != null)
                {
                    ExecutionOutcome o = room.Design.Outcome;
                    details.AppendLine($"FCU ID {o.FcuId.IntegerValue}；面积 {room.Design.RoomAreaSqm:F1} ㎡；支管 DN{room.Design.ActualDn}。");
                    details.AppendLine($"供水：{(o.SupplyTeeConnected ? "已接主管" : o.SupplyBranchCreated ? "支管已生成，未接主管" : "未完成")}；"
                        + $"回水：{(!options.EnableReturnPipe ? "未启用" : o.ReturnTeeConnected ? "已接主管" : "未完成/已跳过")}；"
                        + $"冷凝水：{(!options.EnableCondensate ? "未启用" : o.CondensateConnected ? "已接主管" : "未完成")}。");
                    foreach (string warning in o.Warnings) details.AppendLine("提示：" + warning);
                }
                if (!string.IsNullOrEmpty(room.Error)) details.AppendLine(room.Error);
                details.AppendLine();
            }
            int complete = results.Count(r => r.Status(options) == "连接完成");
            int partial = results.Count(r => r.Design != null) - complete;
            int failed = results.Count(r => r.Design == null && !r.NotRun);
            int notRun = results.Count(r => r.NotRun);
            TaskDialog dialog = new TaskDialog("FCU 多房间执行结果")
            {
                MainInstruction = $"共 {results.Count} 间：连接完成 {complete}，部分完成 {partial}，失败 {failed}，未执行 {notRun}",
                MainContent = results.Count <= 8 ? summary.ToString() : "展开详细信息查看各房间结果。",
                ExpandedContent = details.ToString(),
                FooterText = "仅验证模型连接及本批次管线实体干涉；未验证水力性能、排水坡度、保温和检修净距。"
            };
            dialog.Show();
        }
    }
}
