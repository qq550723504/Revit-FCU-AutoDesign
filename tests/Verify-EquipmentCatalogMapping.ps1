$ErrorActionPreference = 'Stop'
$source = Get-Content (Join-Path $PSScriptRoot '..\Business\EquipmentCatalog\EquipmentCatalogMapping.cs') -Raw -Encoding UTF8
$source = $source -replace '(?m)^using\s+[^;]+;\s*', ''
$checks = @'
namespace FCUAutoDesign.Business.EquipmentCatalog
{
    public static class EquipmentCatalogMappingChecks
    {
        private static int count;
        private static readonly EquipmentCatalogMapping[] Mappings =
        {
            new EquipmentCatalogMapping {
                CatalogVersion = "catalog-1", SeriesId = "FP", ModelCode = "FP-102",
                RevitFamilyTypeKey = "family-guid/type-102"
            },
            new EquipmentCatalogMapping {
                CatalogVersion = "catalog-1", SeriesId = "FP", ModelCode = "FP-68",
                RevitFamilyTypeKey = "family-guid/type-68"
            }
        };

        private static void Check(bool ok, string name)
        {
            if (!ok) throw new System.Exception("FAIL: " + name);
            count++; System.Console.WriteLine("PASS: " + name);
        }

        private static FamilyTypeResolutionResult Resolve(string series, string model, EquipmentCatalogMapping[] mappings = null)
        {
            return new FamilyTypeMappingResolver().Resolve("catalog-1", series, model, mappings ?? Mappings);
        }

        public static void Run()
        {
            var resolved = Resolve("FP", "FP-102");
            Check(resolved.Success && resolved.Mapping.RevitFamilyTypeKey == "family-guid/type-102",
                "Explicit catalog mapping resolves a single family type");

            var missing = Resolve("FP", "FP-85");
            Check(!missing.Success && missing.ErrorCode == FamilyTypeResolutionErrorCode.MissingMapping,
                "Unknown model mapping is rejected");

            var ambiguous = Resolve("FP", "FP-102", new[]
            {
                Mappings[0],
                new EquipmentCatalogMapping {
                    CatalogVersion = "catalog-1", SeriesId = "FP", ModelCode = "FP-102",
                    RevitFamilyTypeKey = "other-family/type-102"
                }
            });
            Check(!ambiguous.Success && ambiguous.ErrorCode == FamilyTypeResolutionErrorCode.AmbiguousMapping,
                "Multiple family mappings are rejected");

            var invalid = Resolve("FP", "FP-102", new[]
            {
                new EquipmentCatalogMapping {
                    CatalogVersion = "catalog-1", SeriesId = "FP", ModelCode = "FP-102",
                    RevitFamilyTypeKey = ""
                }
            });
            Check(!invalid.Success && invalid.ErrorCode == FamilyTypeResolutionErrorCode.InvalidMapping,
                "Blank family type key is rejected");

            var wrongVersion = new FamilyTypeMappingResolver().Resolve(
                "catalog-2", "FP", "FP-102", Mappings);
            Check(!wrongVersion.Success && wrongVersion.ErrorCode == FamilyTypeResolutionErrorCode.MissingMapping,
                "Mapping from another catalog version is not reused");

            var wrongSeries = Resolve("OTHER", "FP-102");
            Check(!wrongSeries.Success && wrongSeries.ErrorCode == FamilyTypeResolutionErrorCode.MissingMapping,
                "Mapping from another series is not reused");

            System.Console.WriteLine(count + " equipment catalog mapping checks passed. Revit family loading is NOT_RUN.");
        }
    }
}
'@
$imports = "using System;`r`nusing System.Collections.Generic;`r`nusing System.Linq;`r`n"
Add-Type -TypeDefinition ($imports + $source + [Environment]::NewLine + $checks)
[FCUAutoDesign.Business.EquipmentCatalog.EquipmentCatalogMappingChecks]::Run()
