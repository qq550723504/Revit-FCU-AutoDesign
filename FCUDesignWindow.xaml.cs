using System;
using System.Collections.Generic;
using System.Windows;

namespace FCUAutoDesign
{
    public partial class FCUDesignWindow : Window
    {
        public double DoorOffsetMm { get; private set; } = 500;
        public bool CenterPlacement { get; private set; } = false;
        public double FcuElevationMm { get; private set; } = 2600;
        public double ValveClearanceMm { get; private set; } = 400;
        public double? MinimumStraightLengthMm { get; private set; }
        public double FlipDropMm { get; private set; } = 150;
        public double CoolingIndexWPerSquareMeter { get; private set; } = 200;
        public bool EnableAutoSizing { get; private set; } = true;
        public bool EnableBusinessRulePreview { get; private set; } = true;
        public bool EnableReturnPipe { get; private set; } = true;
        public bool EnableCondensate { get; private set; } = false;
        public bool BreakCurveAndTee { get; private set; } = true;
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
                MessageBox.Show(this, "请选择 FCU 类型。距离、高度必须是有限正数；最小净直管可留空，填写时必须为正数且小于目标接管距离。冷指标须非负。", "参数无效");
                return;
            }

            EnableAutoSizing = ChkAutoDn.IsChecked == true;
            EnableBusinessRulePreview = ChkBusinessPreview.IsChecked == true;
            EnableReturnPipe = ChkEnableReturn.IsChecked == true;
            CenterPlacement = ChkCenterPlacement.IsChecked == true;
            EnableCondensate = ChkEnableCondensate.IsChecked == true;
            BreakCurveAndTee = ChkBreakCurve.IsChecked == true;

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
            CenterPlacement = ChkCenterPlacement.IsChecked == true;
            FlipDropMm = fDrop;
            CoolingIndexWPerSquareMeter = coolingIndex;
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
