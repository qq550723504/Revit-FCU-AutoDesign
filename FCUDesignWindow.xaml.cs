using System;
using System.Collections.Generic;
using System.Windows;
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
        public bool EnableAutoSizing { get; private set; } = true;
        public bool EnableBusinessRulePreview { get; private set; } = true;
        public bool EnableMultipleFcus { get; private set; } = true;
        public bool EnableReturnPipe { get; private set; } = true;
        public bool EnableCondensate { get; private set; } = false;
        public bool BreakCurveAndTee { get; private set; } = true;
        public bool EnableAutomaticScopeDiscovery { get; private set; } = false;
        public IList<string> AutomaticRoomNameKeywords { get; private set; } = new List<string>();
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
