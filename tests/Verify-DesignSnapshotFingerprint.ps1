$ErrorActionPreference = 'Stop'
$contract = Get-Content (Join-Path $PSScriptRoot '..\Models\DesignRecordContract.cs') -Raw -Encoding UTF8
$fingerprint = Get-Content (Join-Path $PSScriptRoot '..\Services\DesignSnapshotFingerprint.cs') -Raw -Encoding UTF8
$contract = [regex]::Replace($contract, '(?m)^using .*;\r?$', '')
$fingerprint = [regex]::Replace($fingerprint, '(?m)^using .*;\r?$', '')
$harness = @'
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FCUAutoDesign;
public static class Checks
{
    static int count;
    static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); count++; Console.WriteLine("PASS: " + name); }
    static DesignSnapshot Snapshot(double x, string pipeId)
    {
        var snapshot = new DesignSnapshot { Kind = DesignSnapshotKind.Current, Status = DesignSnapshotStatus.Valid };
        var room = new DesignRoomRecord { RoomUniqueId = "room", UnitCount = 1,
            Length = new DesignQuantity(6, DesignUnit.Meter), Height = new DesignQuantity(5, DesignUnit.Meter),
            CoolingLoadDensity = new DesignQuantity(200, DesignUnit.WattPerSquareMeter),
            DesignCoolingLoad = new DesignQuantity(6, DesignUnit.Kilowatt) };
        room.DeviceLogicalIds.Add("device"); room.PipeLogicalIds.Add("pipe"); snapshot.Rooms.Add(room);
        snapshot.Devices.Add(new DesignDeviceRecord { LogicalDeviceId = "device", ElementUniqueId = "element-device",
            CatalogItemId = "FP-102", FamilySymbolUniqueId = "symbol",
            DesignCoolingLoad = new DesignQuantity(6, DesignUnit.Kilowatt),
            RatedCoolingCapacity = new DesignQuantity(7.2, DesignUnit.Kilowatt),
            Position = new DesignPoint(x, .5, 2.6, DesignUnit.Meter, "origin"),
            Orientation = "1,0,0", State = DesignElementState.CreatedByPlugin });
        snapshot.Pipes.Add(new DesignPipeRecord { LogicalPipeId = "pipe", ElementUniqueId = pipeId,
            Role = DesignPipeRole.SupplyBranch, Ownership = DesignPipeOwnership.Room,
            State = DesignElementState.CreatedByPlugin });
        return snapshot;
    }
    public static void Main()
    {
        string first = DesignSnapshotFingerprint.Compute(Snapshot(1.5, "element-pipe"));
        string repeated = DesignSnapshotFingerprint.Compute(Snapshot(1.5, "element-pipe"));
        Check(first == repeated && first.Length == 64, "Same state has stable SHA-256 fingerprint");
        Check(first != DesignSnapshotFingerprint.Compute(Snapshot(2.0, "element-pipe")), "Manual position change invalidates fingerprint");
        Check(first != DesignSnapshotFingerprint.Compute(Snapshot(1.5, "replacement-pipe")), "Pipe replacement invalidates fingerprint");
        var loadChanged = Snapshot(1.5, "element-pipe");
        loadChanged.Devices[0].DesignCoolingLoad = new DesignQuantity(6.5, DesignUnit.Kilowatt);
        Check(first != DesignSnapshotFingerprint.Compute(loadChanged), "Device calculation change invalidates fingerprint");
        Console.WriteLine(count + " snapshot fingerprint checks passed.");
    }
}
'@
$compiler = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\Roslyn\csc.exe'
$testDir = Join-Path ([IO.Path]::GetTempPath()) ('FCU-Fingerprint-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testDir)
$source = Join-Path $testDir 'Checks.cs'; $exe = Join-Path $testDir 'Checks.exe'
[IO.File]::WriteAllText($source, $harness + [Environment]::NewLine + $contract + [Environment]::NewLine + $fingerprint)
& $compiler /nologo /target:exe "/out:$exe" $source
if ($LASTEXITCODE -ne 0) { throw 'Fingerprint harness compilation failed.' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Fingerprint regression failed.' }
