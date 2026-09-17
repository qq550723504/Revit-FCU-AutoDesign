using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using FCUAutoDesign.Agent;
using FCUAutoDesign.Business.RoomSelection;

namespace FCUAutoDesign
{
    public partial class FCUDesignWindow : Window
    {
        public double DoorOffsetMm { get; private set; } = 500;
        public double FcuElevationMm { get; private set; } = 2600;
        public double ValveClearanceMm { get; private set; } = 400;
        public double? MinimumStraightLengthMm { get; private set; }
        public double FlipDropMm { get; private set; } = 150;
        public double CoolingIndexWPerSquareMeter { get; private set; } = 200;
        public bool EnableAutoSizing { get; private set; } = false;
        public bool EnableBusinessRulePreview { get; private set; } = true;
        public bool EnableMultipleFcus { get; private set; } = true;
        public bool EnableReturnPipe { get; private set; } = true;
        public bool EnableCondensate { get; private set; } = false;
        public bool BreakCurveAndTee { get; private set; } = true;
        public bool EnableAutomaticScopeDiscovery { get; private set; } = false;
        public IList<string> AutomaticRoomNameKeywords { get; private set; } = new List<string>();
        public FcuOperationMode OperationMode { get; private set; } = FcuOperationMode.Create;
        public bool IsConfirmed { get; private set; } = false;
        public int? SelectedFcuTypeId => FcuTypePicker.SelectedValue as int?;

        public void SetFcuTypes(IList<KeyValuePair<int, string>> types)
        {
            FcuTypePicker.ItemsSource = types;
            FcuTypePicker.SelectedIndex = types.Count == 1 ? 0 : -1;
        }

        public FCUDesignWindow()
        {
            InitializeComponent();
        }

        private async void BtnAgentPlan_Click(object sender, RoutedEventArgs e)
        {
            string request = (TxtAgentRequest.Text ?? string.Empty).Trim();
            if (request.Length == 0)
            {
                MessageBox.Show(this, "请先描述希望 Agent 分析或调整的内容。", "Agent 请求为空");
                return;
            }

            double coolingIndex;
            if (!TryNonNegative(TxtCoolingIndex.Text, out coolingIndex))
            {
                MessageBox.Show(this, "请先填写有效的非负冷指标。", "Agent 上下文无效");
                return;
            }

            BtnAgentPlan.IsEnabled = false;
            TxtAgentStatus.Text = "正在生成只读建议…";
            try
            {
                AgentRequestContext context = new AgentRequestContext
                {
                    UserRequest = request,
                    CurrentMode = RbRecalculate.IsChecked == true
                        ? FcuOperationMode.Recalculate : FcuOperationMode.Create,
                    CurrentRoomNameKeywords = RoomNameKeywordPolicy.Parse(TxtAutomaticRoomKeywords.Text),
                    CoolingIndexWPerSquareMeter = coolingIndex
                };
                AgentCallResult result;
                using (OpenAiCompatibleAgentClient client =
                    new OpenAiCompatibleAgentClient(AgentConfiguration.FromEnvironment()))
                {
                    result = await client.CreatePlanAsync(context, CancellationToken.None);
                }

                if (result.Status != AgentCallStatus.Success || result.Plan == null)
                {
                    TxtAgentStatus.Text = "未生成建议";
                    MessageBox.Show(this, result.Message ?? "Agent 未返回有效建议。", "Agent 不可用");
                    return;
                }

                string preview = FormatAgentPlan(result.Plan, result.Validation.Mode);
                TxtAgentPreview.Text = preview;
                TxtAgentPreview.Visibility = Visibility.Visible;
                TxtAgentStatus.Text = "建议已通过本地契约校验";
                MessageBoxResult decision = MessageBox.Show(this,
                    preview + Environment.NewLine + Environment.NewLine
                    + "是否将建议回填到当前表单？回填不会执行 Revit 操作。",
                    "确认 Agent 建议", MessageBoxButton.YesNo, MessageBoxImage.Question,
                    MessageBoxResult.No);
                if (decision == MessageBoxResult.Yes)
                {
                    ApplyAgentPlan(result.Plan, result.Validation.Mode);
                    TxtAgentStatus.Text = "已回填表单，尚未执行 Revit 操作";
                }
                else
                {
                    TxtAgentStatus.Text = "建议未回填";
                }
            }
            catch (Exception ex)
            {
                TxtAgentStatus.Text = "生成失败";
                MessageBox.Show(this, "Agent 调用失败：" + ex.Message, "Agent 错误");
            }
            finally
            {
                BtnAgentPlan.IsEnabled = true;
            }
        }

        private void ApplyAgentPlan(AgentPlan plan, AgentPlanMode mode)
        {
            if (plan == null) return;
            if (mode == AgentPlanMode.Create) RbCreate.IsChecked = true;
            else if (mode == AgentPlanMode.Recalculate) RbRecalculate.IsChecked = true;

            IList<string> keywords = plan.room_name_keywords == null
                ? new List<string>()
                : plan.room_name_keywords.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            if (keywords.Count > 0)
            {
                TxtAutomaticRoomKeywords.Text = string.Join(";", keywords);
                ChkAutomaticScope.IsChecked = true;
            }
            if (plan.cooling_index_w_per_square_meter.HasValue)
                TxtCoolingIndex.Text = plan.cooling_index_w_per_square_meter.Value
                    .ToString("0.##", CultureInfo.CurrentCulture);
        }

        private static string FormatAgentPlan(AgentPlan plan, AgentPlanMode mode)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("摘要：" + plan.summary);
            text.AppendLine("模式：" + ModeLabel(mode));
            if (plan.room_name_keywords != null && plan.room_name_keywords.Count > 0)
                text.AppendLine("房间关键词：" + string.Join("、", plan.room_name_keywords));
            if (plan.cooling_index_w_per_square_meter.HasValue)
                text.AppendLine("冷指标建议：" + plan.cooling_index_w_per_square_meter.Value
                    .ToString("0.##", CultureInfo.CurrentCulture) + " W/m²");
            AppendList(text, "观察", plan.observations);
            AppendList(text, "警告", plan.warnings);
            AppendList(text, "禁止操作", plan.blocked_actions);
            string preview = text.ToString().TrimEnd();
            return preview.Length <= 6000 ? preview : preview.Substring(0, 6000) + "…";
        }

        private static string ModeLabel(AgentPlanMode mode)
        {
            if (mode == AgentPlanMode.Create) return "首次生成";
            if (mode == AgentPlanMode.Recalculate) return "修改重算";
            return "仅诊断";
        }

        private static void AppendList(StringBuilder text, string label, IList<string> values)
        {
            if (values == null || values.Count == 0) return;
            text.AppendLine(label + "：" + string.Join("；", values));
        }

        private void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            if (!TryReadParameters())
            {
                MessageBox.Show(this, "请选择 FCU 类型。距离、高度必须是有限正数；最小净直管可留空，填写时必须为正数且小于目标接管距离。冷指标须非负。启用自动发现时，房间名称关键词不能为空。", "参数无效");
                return;
            }

            EnableAutoSizing = ChkAutoDn.IsChecked == true;
            EnableBusinessRulePreview = ChkBusinessPreview.IsChecked == true;
            EnableMultipleFcus = ChkMultipleFcus.IsChecked == true;
            EnableReturnPipe = ChkEnableReturn.IsChecked == true;
            EnableCondensate = ChkEnableCondensate.IsChecked == true;
            BreakCurveAndTee = ChkBreakCurve.IsChecked == true;
            EnableAutomaticScopeDiscovery = ChkAutomaticScope.IsChecked == true;
            OperationMode = RbRecalculate.IsChecked == true
                ? FcuOperationMode.Recalculate : FcuOperationMode.Create;

            IsConfirmed = true;
            DialogResult = true;
        }

        private bool TryReadParameters()
        {
            double dOffset, fElev, vClear, fDrop, coolingIndex;
            if (!SelectedFcuTypeId.HasValue) return false;
            if (!TryPositive(TxtDoorOffset.Text, out dOffset)
                || !TryPositive(TxtFcuElevation.Text, out fElev)
                || !TryPositive(TxtValveClearance.Text, out vClear)
                || !TryPositive(TxtFlipDrop.Text, out fDrop)
                || !TryNonNegative(TxtCoolingIndex.Text, out coolingIndex)) return false;
            double minimum;
            double? minimumStraight = null;
            if (!string.IsNullOrWhiteSpace(TxtMinimumStraight.Text))
            {
                if (!TryPositive(TxtMinimumStraight.Text, out minimum) || minimum >= vClear) return false;
                minimumStraight = minimum;
            }
            DoorOffsetMm = dOffset;
            FcuElevationMm = fElev;
            ValveClearanceMm = vClear;
            MinimumStraightLengthMm = minimumStraight;
            FlipDropMm = fDrop;
            CoolingIndexWPerSquareMeter = coolingIndex;
            IList<string> roomKeywords = RoomNameKeywordPolicy.Parse(TxtAutomaticRoomKeywords.Text);
            if (ChkAutomaticScope.IsChecked == true && roomKeywords.Count == 0) return false;
            AutomaticRoomNameKeywords = roomKeywords;
            return true;
        }

        private static bool TryPositive(string value, out double number)
        {
            return double.TryParse(value, out number) && !double.IsNaN(number)
                && !double.IsInfinity(number) && number > 0;
        }

        private static bool TryNonNegative(string value, out double number)
        {
            return double.TryParse(value, out number) && !double.IsNaN(number)
                && !double.IsInfinity(number) && number >= 0;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            IsConfirmed = false;
            DialogResult = false;
        }
    }
}
