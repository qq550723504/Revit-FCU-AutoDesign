using System;
using System.Windows;

namespace FCUAutoDesign
{
    public partial class FCUDesignWindow : Window
    {
        public double DoorOffsetMm { get; private set; } = 800;
        public double FcuElevationMm { get; private set; } = 2600;
        public double ValveClearanceMm { get; private set; } = 400;
        public double FlipDropMm { get; private set; } = 150;
        public double CondensateSlope { get; private set; } = 0.008;
        public bool EnableAutoSizing { get; private set; } = true;
        public bool EnableReturnPipe { get; private set; } = true;
        public bool EnableCondensate { get; private set; } = true;
        public bool BreakCurveAndTee { get; private set; } = true;
        public bool IsConfirmed { get; private set; } = false;

        public FCUDesignWindow()
        {
            InitializeComponent();
        }

        private void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            if (double.TryParse(TxtDoorOffset.Text, out double dOffset)) DoorOffsetMm = dOffset;
            if (double.TryParse(TxtFcuElevation.Text, out double fElev)) FcuElevationMm = fElev;
            if (double.TryParse(TxtValveClearance.Text, out double vClear)) ValveClearanceMm = vClear;
            if (double.TryParse(TxtFlipDrop.Text, out double fDrop)) FlipDropMm = fDrop;
            if (double.TryParse(TxtCondensateSlope.Text, out double slope)) CondensateSlope = slope / 100.0;

            EnableAutoSizing = ChkAutoDn.IsChecked == true;
            EnableReturnPipe = ChkEnableReturn.IsChecked == true;
            EnableCondensate = ChkEnableCondensate.IsChecked == true;
            BreakCurveAndTee = ChkBreakCurve.IsChecked == true;

            IsConfirmed = true;
            this.Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            IsConfirmed = false;
            this.Close();
        }
    }
}
