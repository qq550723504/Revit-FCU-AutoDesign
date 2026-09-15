using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace FCUAutoDesign
{
    internal class CondensateMainPicker
    {
        public Pipe Pick(UIDocument uidoc, int step)
        {
            while (true)
            {
                // 先允许点击，再明确说明不符合条件的原因，避免分类过滤导致无反馈。
                Reference reference = uidoc.Selection.PickObject(ObjectType.Element,
                    $"【步骤 {step}/{step}】请选择冷凝水主管（将打断接入；ESC 取消整次操作）");
                Element element = uidoc.Document.GetElement(reference);
                Pipe pipe = element as Pipe;
                string reason;
                if (pipe == null)
                {
                    reason = element is RevitLinkInstance
                        ? "选中的是链接模型。当前功能只能修改本项目内的管道，不能打断链接模型中的主管。"
                        : "选中的不是本项目内的普通管道（Pipe）。若点到保温层或管件，请用 Tab 切换到管道本体后再选择。";
                }
                else
                {
                    ElementId typeId = pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId();
                    PipingSystemType system = typeId == null ? null : uidoc.Document.GetElement(typeId) as PipingSystemType;
                    if (system != null && CondensateSystemPolicy.ConnectorTypeFor(system.SystemClassification).HasValue)
                        return pipe;
                    reason = $"管道元素 ID：{pipe.Id.IntegerValue}\n"
                        + $"系统类型：{system?.Name ?? "未指定"}\n"
                        + $"实际系统分类：{system?.SystemClassification.ToString() ?? "未指定"}\n\n"
                        + "冷凝水主管和设备管道端接口必须为卫生设备（Sanitary）分类。"
                        + "管道名称或颜色不能代替系统分类。请核实模型用途后选择相符的主管。";
                }
                TaskDialogResult choice = TaskDialog.Show("该元素暂不能作为冷凝水主管", reason,
                    TaskDialogCommonButtons.Retry | TaskDialogCommonButtons.Cancel);
                if (choice != TaskDialogResult.Retry) return null;
            }
        }
    }
}
