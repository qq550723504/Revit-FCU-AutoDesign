$ErrorActionPreference = 'Stop'
$model = Get-Content (Join-Path $PSScriptRoot '..\Models\DesignRecordContract.cs') -Raw -Encoding UTF8
$model = [regex]::Replace($model, '(?m)^using .*;\r?$', '')
$codec = Get-Content (Join-Path $PSScriptRoot '..\Services\DesignRecordPayloadCodec.cs') -Raw -Encoding UTF8
$codec = [regex]::Replace($codec, '(?m)^using .*;\r?$', '')
$harness = @'
using System;
using System.Linq;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using FCUAutoDesign;
public static class Checks
{
    static int count;
    static void Check(bool value, string name)
    {
        if (!value) throw new Exception("FAIL: " + name);
        count++; Console.WriteLine("PASS: " + name);
    }
    public static void Main()
    {
        var version = new DesignVersion { SchemaVersion = 1, Revision = 1, RuleVersion = "r1" };
        var snapshot = new DesignSnapshot { Kind = DesignSnapshotKind.Baseline,
            Status = DesignSnapshotStatus.Valid, Version = version, CapturedAtUtc = DateTime.UtcNow };
        var room = new DesignRoomRecord { RoomUniqueId = "room-1", UnitCount = 1,
            Length = new DesignQuantity(6, DesignUnit.Meter) };
        room.DeviceLogicalIds.Add("device-1"); room.PipeLogicalIds.Add("pipe-1");
        snapshot.Rooms.Add(room);
        snapshot.Devices.Add(new DesignDeviceRecord { LogicalDeviceId = "device-1", RoomUniqueId = "room-1",
            Position = new DesignPoint(1, 2, 3, DesignUnit.Meter, "internal") });
        var pipe = new DesignPipeRecord { LogicalPipeId = "pipe-1", Ownership = DesignPipeOwnership.Room };
        pipe.ServiceRoomUniqueIds.Add("room-1"); pipe.ConnectedElementUniqueIds.Add("device-1");
        snapshot.Pipes.Add(pipe);
        var source = new DesignRecord { Identity = new DesignIdentity { DesignId = "design-1", HostDocumentId = "doc-1" },
            Version = version, Status = DesignRecordStatus.Committed, LastSuccessfulSnapshot = snapshot };
        var codec = new DesignRecordPayloadCodec();
        string json = codec.Encode(source);
        DesignRecord restored = codec.Decode(json);
        Check(restored.Identity.DesignId == "design-1" && restored.Version.Revision == 1, "Identity and version round-trip");
        Check(restored.LastSuccessfulSnapshot.Rooms.Single().DeviceLogicalIds.Single() == "device-1",
            "Room device association round-trips");
        Check(restored.LastSuccessfulSnapshot.Devices.Single().Position.Z == 3,
            "Device position and unit round-trip");
        Check(restored.LastSuccessfulSnapshot.Pipes.Single().ServiceRoomUniqueIds.Single() == "room-1"
            && restored.LastSuccessfulSnapshot.Pipes.Single().ConnectedElementUniqueIds.Single() == "device-1",
            "Pipe ownership associations round-trip");
        Console.WriteLine(count + " design record serialization checks passed. Revit persistence and Undo/Redo: NOT_RUN.");
    }
}
'@
$compiler = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\Roslyn\csc.exe'
$testDir = Join-Path ([IO.Path]::GetTempPath()) ('FCU-DesignRecord-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testDir)
$source = Join-Path $testDir 'Checks.cs'
$exe = Join-Path $testDir 'Checks.exe'
[IO.File]::WriteAllText($source, $harness + [Environment]::NewLine + $model + [Environment]::NewLine + $codec)
& $compiler /nologo /target:exe /reference:System.Web.Extensions.dll "/out:$exe" $source
if ($LASTEXITCODE -ne 0) { throw 'Design record serialization harness compilation failed.' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Design record serialization regression failed.' }
