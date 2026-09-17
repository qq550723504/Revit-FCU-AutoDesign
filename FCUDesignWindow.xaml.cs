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
        private AgentPlan pendingAgentPlan;
        private AgentPlanMode pendingAgentMode;
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
                MessageBox.Show(this, "请先描述希望 AI 分析或调整的内容。", "AI 请求为空");
                return;
            }

            double coolingIndex, doorOffset, fcuElevation;
            if (!TryNonNegative(TxtCoolingIndex.Text, out coolingIndex)
                || !TryPositive(TxtDoorOffset.Text, out doorOffset)
                || !TryPositive(TxtFcuElevation.Text, out fcuElevation))
            {
                MessageBox.Show(this, "请先填写有效的冷指标、距墙距离和安装高度。", "AI 上下文无效");
                return;
            }

            BtnAgentPlan.IsEnabled = false;
            pendingAgentPlan = null;
            BtnApplyAgentPlan.Visibility = Visibility.Collapsed;
            TxtAgentStatus.Text = "正在生成方案…";
            try
            {
                AgentRequestContext context = new AgentRequestContext
                {
                    UserRequest = request,
                    CurrentMode = RbRecalculate.IsChecked == true
                        ? FcuOperationMode.Recalculate : FcuOperationMode.Create,
                    CurrentRoomNameKeywords = RoomNameKeywordPolicy.Parse(TxtAutomaticRoomKeywords.Text),
                    CoolingIndexWPerSquareMeter = coolingIndex,
                    DoorOffsetMm = doorOffset,
                    FcuElevationMm = fcuElevation,
                    EnableMultipleFcus = ChkMultipleFcus.IsChecked == true,
                    EnableAutomaticScopeDiscovery = ChkAutomaticScope.IsChecked == true,
                    EnableReturnPipe = ChkEnableReturn.IsChecked == true,
                    EnableCondensate = ChkEnableCondensate.IsChecked == true
                };
                AgentCallResult result;
                using (OpenAiCompatibleAgentClient client =
                    new OpenAiCompatibleAgentClient(AgentConfiguration.FromEnvironment()))
                {
                    result = await client.CreatePlanAsync(context, CancellationToken.None);
                }

                if (result.Status != AgentCallStatus.Success || result.Plan == null)
                {
                    TxtAgentStatus.Text = "未生成方案";
                    MessageBox.Show(this, result.Message ?? "AI 未返回有效方案。", "AI 不可用");
                    return;
                }

                string preview = FormatAgentPlan(result.Plan, result.Validation.Mode);
                TxtAgentPreview.Text = preview;
                TxtAgentPreview.Visibility = Visibility.Visible;
                TxtAgentStatus.Text = "方案已通过本地契约校验";
                if (result.Validation.RequiresClarification)
                {
                    TxtAgentStatus.Text = "请根据预览中的澄清项修改指令，当前方案未回填";
                    return;
                }
                if (result.Validation.Mode == AgentPlanMode.Diagnose)
                {
                    TxtAgentStatus.Text = "诊断完成，无需回填表单";
                    return;
                }
                pendingAgentPlan = result.Plan;
                pendingAgentMode = result.Validation.Mode;
                BtnApplyAgentPlan.Visibility = Visibility.Visible;
                TxtAgentStatus.Text = "请检查方案后点击“应用方案”";
            }
            catch (Exception ex)
            {
                TxtAgentStatus.Text = "生成失败";
                MessageBox.Show(this, "AI 调用失败：" + ex.Message, "AI 错误");
            }
            finally
            {
                BtnAgentPlan.IsEnabled = true;
            }
        }

        private void BtnApplyAgentPlan_Click(object sender, RoutedEventArgs e)
        {
            if (!ApplyAgentPlan(pendingAgentPlan, pendingAgentMode)) return;
            pendingAgentPlan = null;
            BtnApplyAgentPlan.Visibility = Visibility.Collapsed;
            TxtAgentStatus.Text = "已回填表单，尚未执行 Revit 操作";
        }

        private bool ApplyAgentPlan(AgentPlan plan, AgentPlanMode mode)
        {
            if (plan == null || mode == AgentPlanMode.Diagnose
                || (plan.clarifications != null && plan.clarifications.Count > 0))
                return false;
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
            if (plan.door_offset_mm.HasValue)
                TxtDoorOffset.Text = plan.door_offset_mm.Value
                    .ToString("0.##", CultureInfo.CurrentCulture);
            if (plan.fcu_elevation_mm.HasValue)
                TxtFcuElevation.Text = plan.fcu_elevation_mm.Value
                    .ToString("0.##", CultureInfo.CurrentCulture);
            if (plan.enable_multiple_fcus.HasValue)
                ChkMultipleFcus.IsChecked = plan.enable_multiple_fcus.Value;
            if (plan.enable_automatic_scope_discovery.HasValue)
                ChkAutomaticScope.IsChecked = plan.enable_automatic_scope_discovery.Value;
            if (plan.connect_return.HasValue)
                ChkEnableReturn.IsChecked = plan.connect_return.Value;
            if (plan.connect_condensate.HasValue)
                ChkEnableCondensate.IsChecked = plan.connect_condensate.Value;
            return true;
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
            if (plan.door_offset_mm.HasValue)
                text.AppendLine("距门侧墙：" + plan.door_offset_mm.Value
                    .ToString("0.##", CultureInfo.CurrentCulture) + " mm");
            if (plan.fcu_elevation_mm.HasValue)
                text.AppendLine("FCU安装高度：" + plan.fcu_elevation_mm.Value
                    .ToString("0.##", CultureInfo.CurrentCulture) + " mm");
            AppendFlag(text, "多台均布", plan.enable_multiple_fcus);
            AppendFlag(text, "自动选房及主管", plan.enable_automatic_scope_discovery);
            AppendFlag(text, "连接供水", plan.connect_supply);
            AppendFlag(text, "连接回水", plan.connect_return);
            AppendFlag(text, "连接冷凝水", plan.connect_condensate);
            AppendList(text, "需要澄清", plan.clarifications);
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

        private static void AppendFlag(StringBuilder text, string label, bool? value)
        {
            if (value.HasValue) text.AppendLine(label + "：" + (value.Value ? "是" : "否"));
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
