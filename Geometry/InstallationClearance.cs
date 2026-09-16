using System;

namespace FCUAutoDesign
{
    internal static class InstallationClearance
    {
        public static void ValidateMinimum(double? minimum)
        {
            if (minimum.HasValue && (double.IsNaN(minimum.Value) || double.IsInfinity(minimum.Value) || minimum <= 0))
                throw new InvalidOperationException("指定的最小净直管长度必须为有限正数。");
        }

        public static bool MeetsMinimum(double actual, double? minimum)
        {
            ValidateMinimum(minimum);
            if (double.IsNaN(actual) || double.IsInfinity(actual) || actual <= 0) return false;
            return !minimum.HasValue || actual + 1e-7 >= minimum.Value;
        }
    }
}
