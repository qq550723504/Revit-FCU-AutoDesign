$ErrorActionPreference = 'Stop'
$modelSource = Get-Content (Join-Path $PSScriptRoot '..\Domain\EquipmentSelection\EquipmentSelectionModels.cs') -Raw -Encoding UTF8
$calculatorSource = Get-Content (Join-Path $PSScriptRoot '..\Domain\EquipmentSelection\EquipmentSelectionCalculator.cs') -Raw -Encoding UTF8
$plannerSource = Get-Content (Join-Path $PSScriptRoot '..\Domain\EquipmentSelection\EquipmentPlacementPlanner.cs') -Raw -Encoding UTF8
$modelSource = $modelSource -replace '(?m)^using\s+[^;]+;\s*', ''
$calculatorSource = $calculatorSource -replace '(?m)^using\s+[^;]+;\s*', ''
$plannerSource = $plannerSource -replace '(?m)^using\s+[^;]+;\s*', ''
$checks = @'
namespace FCUAutoDesign.Business.EquipmentSelection
{
    public static class EquipmentSelectionChecks
    {
        private static int count;
        private static readonly EquipmentSelectionCalculator Calculator = new EquipmentSelectionCalculator();
        private static readonly EquipmentCatalogEntry[] Catalog =
        {
            new EquipmentCatalogEntry { SeriesId = "FP", ModelCode = "FP-136", RatedCoolingCapacityKw = 6.95 },
            new EquipmentCatalogEntry { SeriesId = "FP", ModelCode = "FP-102", RatedCoolingCapacityKw = 5.30 },
            new EquipmentCatalogEntry { SeriesId = "FP", ModelCode = "FP-85", RatedCoolingCapacityKw = 4.23 },
            new EquipmentCatalogEntry { SeriesId = "FP", ModelCode = "FP-68", RatedCoolingCapacityKw = 3.50 },
            new EquipmentCatalogEntry { SeriesId = "FP", ModelCode = "FP-51", RatedCoolingCapacityKw = 2.70 },
            new EquipmentCatalogEntry { SeriesId = "FP", ModelCode = "FP-34", RatedCoolingCapacityKw = 1.80 }
        };

        private static EquipmentSelectionInput Input(double length, double width = 5, double q = 200)
        {
            var input = EquipmentSelectionInput.CreateDefault();
            input.SeriesId = "FP"; input.CatalogVersion = "prd-v1";
            input.LengthM = length; input.WidthM = width;
            input.CoolingIndexWPerSquareMeter = q;
            return input;
        }

        private static void Check(bool ok, string name)
        {
            if (!ok) throw new System.Exception("FAIL: " + name);
            count++; System.Console.WriteLine("PASS: " + name);
        }

        private static void Reject(EquipmentSelectionInput input, EquipmentSelectionErrorCode code, string name)
        {
            var result = Calculator.Calculate(input, Catalog);
            Check(!result.Success && result.ErrorCode == code, name);
        }

        public static void Run()
        {
            var planner = new EquipmentPlacementPlanner();
            for (int n = 1; n <= 8; n++)
            {
                double length = n * 5.0;
                var points = planner.Build(length, 4, -3, 2.6, 0.5, n);
                Check(points.Count == n && Math.Abs(points[0].XAlongLengthM - 2.5) < 1e-9
                    && Math.Abs(length - points[n - 1].XAlongLengthM - 2.5) < 1e-9
                    && points.All(p => Math.Abs(p.AbsoluteElevationM + .4) < 1e-9 && p.YFromReferenceEdgeM == .5)
                    && points.Zip(points.Skip(1), (a, b) => Math.Abs(b.XAlongLengthM - a.XAlongLengthM - 5) < 1e-9).All(x => x),
                    "Half-edge spacing, elevation and wall offset hold for " + n + " units");
            }
            bool rejected = false;
            try { planner.Build(10, 4, double.NaN, 2.6, .5, 2); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected, "Invalid elevation cannot produce model placement points");
            var e01 = Calculator.Calculate(Input(5, 5), Catalog);
            Check(e01.Success && e01.AreaSquareMeters == 25 && e01.DesignLoadKw == 5
                && e01.UnitCount == 1 && e01.SelectedEquipment.ModelCode == "FP-102", "5x5 selects FP-102");

            var e02 = Calculator.Calculate(Input(6, 5), Catalog);
            Check(e02.Success && e02.UnitCount == 2 && e02.UnitDesignLoadKw == 3
                && e02.SelectedEquipment.ModelCode == "FP-68"
                && System.Math.Abs(e02.PlacementPoints[0].XAlongLengthM - 1.5) < 1e-9
                && System.Math.Abs(e02.PlacementPoints[1].XAlongLengthM - 4.5) < 1e-9,
                "6x5 produces quarter-edge points from the customer rule");

            var e03Points = Calculator.Calculate(Input(15, 5), Catalog);
            Check(e03Points.Success && e03Points.UnitCount == 3
                && System.Math.Abs(e03Points.PlacementPoints[0].XAlongLengthM - 2.5) < 1e-9
                && System.Math.Abs(e03Points.PlacementPoints[1].XAlongLengthM - 7.5) < 1e-9
                && System.Math.Abs(e03Points.PlacementPoints[2].XAlongLengthM - 12.5) < 1e-9,
                "15x5 produces L/6, L/2 and 5L/6 points");

            var e03 = Calculator.Calculate(Input(10, 4), Catalog);
            Check(e03.Success && e03.UnitCount == 2 && e03.SelectedEquipment.ModelCode == "FP-85",
                "10x4 selects FP-85");

            var e04 = Calculator.Calculate(Input(5, 5, 212), Catalog);
            Check(e04.Success && System.Math.Abs(e04.UnitDesignLoadKw - 5.30) < 1e-9
                && e04.SelectedEquipment.ModelCode == "FP-102", "Exact capacity match is accepted");

            Reject(Input(5, 5, 300), EquipmentSelectionErrorCode.CapacityNotFound,
                "Insufficient maximum capacity is rejected");
            Check(Calculator.Calculate(Input(5), Catalog).UnitCount == 1, "L=5 uses one unit");
            Check(Calculator.Calculate(Input(5.0001), Catalog).UnitCount == 2, "L>5 uses two units");
            Check(Calculator.Calculate(Input(40), Catalog).UnitCount == 8, "L=40 uses eight units");
            Reject(Input(40.0001), EquipmentSelectionErrorCode.LengthOutsideSupportedRange,
                "L>40 is not extrapolated");
            Reject(Input(40.0000000001), EquipmentSelectionErrorCode.LengthOutsideSupportedRange,
                "Floating-point boundary cannot silently create a ninth unit");
            for (double boundary = 5; boundary <= 40; boundary += 5)
            {
                var exact = Calculator.Calculate(Input(boundary), Catalog);
                Check(exact.Success && exact.UnitCount == (int)(boundary / 5),
                    "Exact L boundary " + boundary + " has the documented unit count");
                if (boundary < 40)
                {
                    var above = Calculator.Calculate(Input(boundary + 0.0001), Catalog);
                    Check(above.Success && above.UnitCount == (int)(boundary / 5) + 1,
                        "Just above L boundary " + boundary + " increments unit count");
                }
            }
            Reject(Input(5, 5, 0), EquipmentSelectionErrorCode.ZeroLoadRequiresConfirmation,
                "Zero load requires explicit product decision");
            Reject(Input(0), EquipmentSelectionErrorCode.InvalidLength, "Non-positive length is rejected");
            Reject(Input(5, 0), EquipmentSelectionErrorCode.InvalidWidth, "Non-positive width is rejected");

            var tied = new[]
            {
                new EquipmentCatalogEntry { SeriesId = "FP", ModelCode = "A", RatedCoolingCapacityKw = 5.0 },
                new EquipmentCatalogEntry { SeriesId = "FP", ModelCode = "B", RatedCoolingCapacityKw = 5.0 }
            };
            var ambiguous = Calculator.Calculate(Input(5), tied);
            Check(!ambiguous.Success && ambiguous.ErrorCode == EquipmentSelectionErrorCode.AmbiguousCapacityMatch,
                "Equal capacity models require explicit choice");
            var selected = Calculator.Calculate(Input(5), tied, "B");
            Check(selected.Success && selected.SelectedEquipment.ModelCode == "B", "Explicit model choice resolves tie");

            Check(System.Math.Abs(e01.PlacementPoints[0].YFromReferenceEdgeM - 0.5) < 1e-9
                && System.Math.Abs(e01.PlacementPoints[0].AbsoluteElevationM - 2.5) < 1e-9,
                "Placement offset and absolute elevation are explicit");
            System.Console.WriteLine(count + " equipment selection checks passed. Revit 2020 model acceptance is NOT_RUN.");
        }
    }
}
'@
$imports = "using System;`r`nusing System.Collections.Generic;`r`nusing System.Linq;`r`n"
Add-Type -TypeDefinition ($imports + $modelSource + [Environment]::NewLine + $plannerSource + [Environment]::NewLine + $calculatorSource + [Environment]::NewLine + $checks)
[FCUAutoDesign.Business.EquipmentSelection.EquipmentSelectionChecks]::Run()
