$ErrorActionPreference = 'Stop'
$source = Get-Content (Join-Path $PSScriptRoot '..\Business\Hydraulic\HydraulicTable.cs') -Raw -Encoding UTF8
$source = $source -replace '(?m)^using\s+[^;]+;\s*', ''
$checks = @'
namespace FCUAutoDesign.Business.Hydraulic
{
    public static class HydraulicTableChecks
    {
        private static int count;
        private static HydraulicTableDefinition ValidTable()
        {
            var table = new HydraulicTableDefinition {
                TableId = "synthetic-test-load", Version = "test-only-1",
                InputQuantity = HydraulicMeasureKind.DesignCoolingLoad,
                Unit = HydraulicUnit.Kilowatt, LoadBasis = "synthetic test only"
            };
            table.Bands.Add(new HydraulicTableBand {
                LowerBound = 0, LowerInclusive = true, UpperBound = 2,
                UpperInclusive = false, NominalDiameter = "DN20"
            });
            table.Bands.Add(new HydraulicTableBand {
                LowerBound = 2, LowerInclusive = true, UpperBound = 5,
                UpperInclusive = false, NominalDiameter = "DN25"
            });
            table.Bands.Add(new HydraulicTableBand {
                LowerBound = 5, LowerInclusive = true, UpperBound = 10,
                UpperInclusive = true, NominalDiameter = "DN32"
            });
            return table;
        }

        private static void Check(bool ok, string name)
        {
            if (!ok) throw new System.Exception("FAIL: " + name);
            count++; System.Console.WriteLine("PASS: " + name);
        }

        private static void CheckError(HydraulicTableDefinition table, HydraulicTableErrorCode code, string name)
        {
            var result = new HydraulicTableValidator().Validate(table);
            Check(!result.IsValid && result.ErrorCode == code, name);
        }

        public static void Run()
        {
            var table = ValidTable();
            var validator = new HydraulicTableValidator();
            Check(validator.Validate(table).IsValid, "Synthetic table validates");
            var lookup = new HydraulicTableLookup();
            Check(lookup.Lookup(table, 2).Success && lookup.Lookup(table, 2).Band.NominalDiameter == "DN25",
                "Lower boundary belongs to the next closed interval");
            Check(lookup.Lookup(table, 5).Success && lookup.Lookup(table, 5).Band.NominalDiameter == "DN32",
                "Second lower boundary is resolved deterministically");
            Check(lookup.Lookup(table, 10).Success && lookup.Lookup(table, 10).Band.NominalDiameter == "DN32",
                "Inclusive final upper boundary is accepted");
            Check(!lookup.Lookup(table, 10.001).Success
                && lookup.Lookup(table, 10.001).ErrorCode == HydraulicTableErrorCode.NoMatchingBand,
                "Quantity outside the table is not guessed");

            var overlap = ValidTable();
            overlap.Bands[1].LowerInclusive = true;
            overlap.Bands[0].UpperInclusive = true;
            CheckError(overlap, HydraulicTableErrorCode.Overlap, "Inclusive touching bounds are overlap");

            var gap = ValidTable();
            gap.Bands[1].LowerBound = 2.1;
            CheckError(gap, HydraulicTableErrorCode.Gap, "Missing interval is rejected");

            var duplicate = ValidTable();
            duplicate.Bands[2].NominalDiameter = "dn25";
            CheckError(duplicate, HydraulicTableErrorCode.DuplicateDiameter, "Duplicate DN is rejected");

            var blank = ValidTable();
            blank.Bands[1].NominalDiameter = "";
            CheckError(blank, HydraulicTableErrorCode.InvalidRow, "Blank table row is rejected");

            var negative = ValidTable();
            negative.Bands[0].LowerBound = -1;
            CheckError(negative, HydraulicTableErrorCode.InvalidBounds, "Negative lower bound is rejected");

            var missingMetadata = ValidTable();
            missingMetadata.LoadBasis = "";
            CheckError(missingMetadata, HydraulicTableErrorCode.MissingMetadata,
                "Missing load basis is rejected");

            var old = ValidTable();
            old.Version = "old-version";
            var updated = ValidTable();
            updated.Version = "new-version";
            Check(old.Version != updated.Version, "Table versions remain explicit and distinguishable");
            System.Console.WriteLine(count + " hydraulic table checks passed. Production engineering data and Revit pipe updates are NOT_RUN.");
        }
    }
}
'@
$imports = "using System;`r`nusing System.Collections.Generic;`r`nusing System.Linq;`r`n"
Add-Type -TypeDefinition ($imports + $source + [Environment]::NewLine + $checks)
[FCUAutoDesign.Business.Hydraulic.HydraulicTableChecks]::Run()
