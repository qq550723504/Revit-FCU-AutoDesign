using System;

namespace FCUAutoDesign.Business.EquipmentSelection
{
    public static class RoomLoadCalculator
    {
        public static double AreaSquareMeters(double lengthM, double widthM)
        {
            RequirePositive(lengthM, "lengthM");
            RequirePositive(widthM, "widthM");
            return lengthM * widthM;
        }

        public static double DesignLoadKilowatts(double lengthM, double widthM,
            double coolingIndexWPerSquareMeter)
        {
            if (double.IsNaN(coolingIndexWPerSquareMeter)
                || double.IsInfinity(coolingIndexWPerSquareMeter)
                || coolingIndexWPerSquareMeter < 0)
                throw new ArgumentOutOfRangeException("coolingIndexWPerSquareMeter");
            return AreaSquareMeters(lengthM, widthM) * coolingIndexWPerSquareMeter / 1000.0;
        }

        private static void RequirePositive(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                throw new ArgumentOutOfRangeException(name);
        }
    }
}
