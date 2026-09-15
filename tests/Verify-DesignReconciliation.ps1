$ErrorActionPreference = 'Stop'
$contractSource = Get-Content (Join-Path $PSScriptRoot '..\Models\DesignRecordContract.cs') -Raw -Encoding UTF8
$plannerSource = Get-Content (Join-Path $PSScriptRoot '..\Business\DesignReconciliation\DesignReconciliation.cs') -Raw -Encoding UTF8
$contractSource = $contractSource -replace '(?m)^using\s+[^;]+;\s*', ''
$plannerSource = $plannerSource -replace '(?m)^using\s+[^;]+;\s*', ''
$checks = @'
namespace FCUAutoDesign.Business.DesignReconciliation
{
    public static class DesignReconciliationChecks
    {
        private static int count;

        private static DesignVersion Version(int revision)
        {
            return new DesignVersion { SchemaVersion = 1, Revision = revision,
                RuleVersion = "rules-1", EquipmentCatalogVersion = "catalog-1",
                HydraulicTableVersion = "hydraulic-unconfirmed" };
        }

        private static DesignDeviceRecord Device(string id, double load, double x, string model = "FP-102")
        {
            return new DesignDeviceRecord {
                LogicalDeviceId = id, ElementUniqueId = "element-" + id,
                RoomUniqueId = "room-1", CatalogItemId = model,
                FamilySymbolUniqueId = "family-" + model,
                DesignCoolingLoad = new DesignQuantity(load, DesignUnit.Kilowatt),
                RatedCoolingCapacity = new DesignQuantity(5.3, DesignUnit.Kilowatt),
                Position = new DesignPoint(x, 0.5, 2.5, DesignUnit.Meter, "room-local"),
                Orientation = "north", State = DesignElementState.Managed
            };
        }

        private static DesignSnapshot Snapshot(DesignSnapshotKind kind, int revision, string fingerprint, params DesignDeviceRecord[] devices)
        {
            var snapshot = new DesignSnapshot { Kind = kind, Status = DesignSnapshotStatus.Valid,
                Version = Version(revision), Fingerprint = fingerprint };
            foreach (var device in devices) snapshot.Devices.Add(device);
            return snapshot;
        }

        private static void Check(bool ok, string name)
        {
            if (!ok) throw new System.Exception("FAIL: " + name);
            count++; System.Console.WriteLine("PASS: " + name);
        }

        private static ReconciliationPlan Plan(DesignSnapshot baseline, DesignSnapshot current, DesignSnapshot desired)
        {
            return new DesignReconciliationPlanner().BuildPlan(new ReconciliationRequest {
                Baseline = baseline, Current = current, Desired = desired
            });
        }

        public static void Run()
        {
            var same = Plan(Snapshot(DesignSnapshotKind.Baseline, 1, "fp-1", Device("d1", 5, 1)),
                Snapshot(DesignSnapshotKind.Current, 1, "fp-1", Device("d1", 5, 1)),
                Snapshot(DesignSnapshotKind.Desired, 2, "desired-1", Device("d1", 5, 1)));
            Check(same.IsApplicable && same.Items.Count == 0, "Identical rerun produces no changes");

            var loadChanged = Device("d1", 6, 1);
            var update = Plan(Snapshot(DesignSnapshotKind.Baseline, 1, "fp-1", Device("d1", 5, 1)),
                Snapshot(DesignSnapshotKind.Current, 1, "fp-1", Device("d1", 5, 1)),
                Snapshot(DesignSnapshotKind.Desired, 2, "desired-2", loadChanged));
            Check(update.IsApplicable && update.Items.Count == 1
                && update.Items[0].Action == ReconciliationAction.Update
                && update.Items[0].ChangedFields.Contains("DesignCoolingLoad"),
                "Algorithm-only load change creates an update");

            var moved = Device("d1", 5, 2);
            var preserveMove = Plan(Snapshot(DesignSnapshotKind.Baseline, 1, "fp-1", Device("d1", 5, 1)),
                Snapshot(DesignSnapshotKind.Current, 1, "fp-2", moved),
                Snapshot(DesignSnapshotKind.Desired, 2, "desired-3", loadChanged));
            Check(preserveMove.IsApplicable && preserveMove.Items[0].Action == ReconciliationAction::Update,
                "Manual position change is preserved while load updates");

            var conflictDesired = Device("d1", 5, 3);
            var conflict = Plan(Snapshot(DesignSnapshotKind.Baseline, 1, "fp-1", Device("d1", 5, 1)),
                Snapshot(DesignSnapshotKind.Current, 1, "fp-2", moved),
                Snapshot(DesignSnapshotKind.Desired, 2, "desired-4", conflictDesired));
            Check(!conflict.IsApplicable && conflict.Items[0].Action == ReconciliationAction.Conflict
                && conflict.Items[0].ChangedFields.Contains("Position"),
                "Concurrent manual and algorithm position changes conflict");

            var deleted = Plan(Snapshot(DesignSnapshotKind.Baseline, 1, "fp-1", Device("d1", 5, 1)),
                Snapshot(DesignSnapshotKind.Current, 1, "fp-3"),
                Snapshot(DesignSnapshotKind.Desired, 2, "desired-5", Device("d1", 5, 1)));
            Check(!deleted.IsApplicable && deleted.Items[0].Action == ReconciliationAction.Conflict,
                "Manually deleted managed device is not recreated");

            var removed = Plan(Snapshot(DesignSnapshotKind.Baseline, 1, "fp-1", Device("d1", 5, 1)),
                Snapshot(DesignSnapshotKind.Current, 1, "fp-1", Device("d1", 5, 1)),
                Snapshot(DesignSnapshotKind.Desired, 2, "desired-6"));
            Check(removed.IsApplicable && removed.Items[0].Action == ReconciliationAction.DeleteCandidate,
                "Reduced desired set produces an explicit delete candidate");

            var newDevice = Plan(Snapshot(DesignSnapshotKind.Baseline, 1, "fp-1"),
                Snapshot(DesignSnapshotKind.Current, 1, "fp-1"),
                Snapshot(DesignSnapshotKind.Desired, 2, "desired-7", Device("d2", 3, 2)));
            Check(newDevice.IsApplicable && newDevice.Items[0].Action == ReconciliationAction.Create,
                "New desired device produces a create plan");

            var unmanaged = Plan(Snapshot(DesignSnapshotKind.Baseline, 1, "fp-1"),
                Snapshot(DesignSnapshotKind.Current, 1, "fp-1", Device("legacy", 5, 1)),
                Snapshot(DesignSnapshotKind.Desired, 2, "desired-8"));
            Check(unmanaged.IsApplicable && unmanaged.Items[0].Action == ReconciliationAction.Unmanaged,
                "Unmanaged current device is left untouched");

            var unownedDesired = Plan(Snapshot(DesignSnapshotKind.Baseline, 1, "fp-1"),
                Snapshot(DesignSnapshotKind.Current, 1, "fp-1", Device("legacy", 5, 1)),
                Snapshot(DesignSnapshotKind.Desired, 2, "desired-8b", Device("legacy", 6, 1)));
            Check(!unownedDesired.IsApplicable && unownedDesired.Items[0].Action == ReconciliationAction.Conflict
                && unownedDesired.Items[0].ChangedFields.Contains("UnownedElement"),
                "Existing unowned device is not silently claimed");

            var sameManualChange = Plan(Snapshot(DesignSnapshotKind.Baseline, 1, "fp-1", Device("d1", 5, 1)),
                Snapshot(DesignSnapshotKind.Current, 1, "fp-2", moved),
                Snapshot(DesignSnapshotKind.Desired, 2, "desired-8c", moved));
            Check(sameManualChange.IsApplicable && sameManualChange.Items[0].Action == ReconciliationAction.NoChange,
                "Matching current and desired manual change is not a conflict");

            var stale = new DesignReconciliationPlanner().BuildPlan(new ReconciliationRequest {
                Baseline = Snapshot(DesignSnapshotKind.Baseline, 1, "fp-1", Device("d1", 5, 1)),
                Current = Snapshot(DesignSnapshotKind.Current, 2, "fp-new", Device("d1", 5, 1)),
                Desired = Snapshot(DesignSnapshotKind.Desired, 3, "desired-9", Device("d1", 6, 1)),
                ExpectedCurrentRevision = 1, ExpectedCurrentFingerprint = "fp-1"
            });
            Check(!stale.IsApplicable && stale.ErrorCode == DesignErrorCode.StalePreview,
                "Changed model invalidates an old preview");

            System.Console.WriteLine(count + " design reconciliation checks passed. Revit apply, transaction rollback, and model acceptance are NOT_RUN.");
        }
    }
}
'@
$checks = $checks.Replace('ReconciliationAction::Update', 'ReconciliationAction.Update')
$imports = "using System;`r`nusing System.Collections.Generic;`r`nusing System.Linq;`r`n"
Add-Type -TypeDefinition ($imports + $contractSource + [Environment]::NewLine + $plannerSource + [Environment]::NewLine + $checks)
[FCUAutoDesign.Business.DesignReconciliation.DesignReconciliationChecks]::Run()
