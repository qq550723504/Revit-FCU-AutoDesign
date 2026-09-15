param([string]$SourcePath = (Join-Path $PSScriptRoot '..\Models\DesignRecordContract.cs'))
$ErrorActionPreference = 'Stop'

# This is a pure contract test. It compiles only the Revit-independent source and
# therefore does not claim Revit model persistence or transaction coverage.
$source = Get-Content -LiteralPath (Resolve-Path $SourcePath) -Raw -Encoding UTF8
Add-Type -TypeDefinition $source -Language CSharp

$checks = 0
function Assert-True([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "FAIL: $Name" }
    $script:checks++
    Write-Output "PASS: $Name"
}

$identity = New-Object FCUAutoDesign.DesignIdentity
$identity.DesignId = 'design-001'
$identity.HostDocumentId = 'document-001'
$version = New-Object FCUAutoDesign.DesignVersion
$version.SchemaVersion = 1
$version.Revision = 7
$version.RuleVersion = 'fcu-rules-1'
$version.EquipmentCatalogVersion = 'catalog-1'
$version.HydraulicTableVersion = 'unconfirmed'

$snapshot = New-Object FCUAutoDesign.DesignSnapshot
$snapshot.Kind = [FCUAutoDesign.DesignSnapshotKind]::Baseline
$snapshot.Status = [FCUAutoDesign.DesignSnapshotStatus]::Valid
$snapshot.Version = $version
$snapshot.CapturedAtUtc = [DateTime]::UtcNow
$snapshot.Rooms.Add((New-Object FCUAutoDesign.DesignRoomRecord))
$snapshot.Rooms[0].RoomUniqueId = 'room-001'
$snapshot.Rooms[0].DeviceLogicalIds.Add('device-001')
$snapshot.Rooms[0].PipeLogicalIds.Add('pipe-001')
$snapshot.Devices.Add((New-Object FCUAutoDesign.DesignDeviceRecord))
$snapshot.Devices[0].LogicalDeviceId = 'device-001'
$snapshot.Devices[0].RoomUniqueId = 'room-001'
$pipe = New-Object FCUAutoDesign.DesignPipeRecord
$pipe.LogicalPipeId = 'pipe-001'
$pipe.Ownership = [FCUAutoDesign.DesignPipeOwnership]::SharedNetwork
$pipe.ServiceRoomUniqueIds.Add('room-001')
$pipe.ServiceRoomUniqueIds.Add('room-002')
$snapshot.Pipes.Add($pipe)

$record = New-Object FCUAutoDesign.DesignRecord
$record.Identity = $identity
$record.Version = $version
$record.Status = [FCUAutoDesign.DesignRecordStatus]::Committed
$record.LastSuccessfulSnapshot = $snapshot

Assert-True ($record.Identity.HostDocumentId -eq 'document-001') 'Design identity contains host document identity'
Assert-True ($record.LastSuccessfulSnapshot.Kind -eq 'Baseline') 'Successful snapshot is explicitly a baseline'
Assert-True ($record.LastSuccessfulSnapshot.Devices[0].RoomUniqueId -eq 'room-001') 'Device is associated with a room'
Assert-True ($record.LastSuccessfulSnapshot.Rooms[0].DeviceLogicalIds[0] -eq 'device-001') 'Room records its managed device identity'
Assert-True ($record.LastSuccessfulSnapshot.Pipes[0].ServiceRoomUniqueIds.Count -eq 2) 'Shared pipe can serve multiple rooms'
Assert-True ($record.LastSuccessfulSnapshot.Rooms[0].PipeLogicalIds[0] -eq 'pipe-001') 'Room records its pipe association'
Assert-True ($record.LastSuccessfulSnapshot.Pipes[0].Ownership -eq [FCUAutoDesign.DesignPipeOwnership]::SharedNetwork) 'Shared pipe is not room-owned'

$quantity = New-Object FCUAutoDesign.DesignQuantity(5.0, [FCUAutoDesign.DesignUnit]::Kilowatt)
Assert-True ($quantity.Unit -eq [FCUAutoDesign.DesignUnit]::Kilowatt) 'Quantities retain explicit units'
$invalidUnitRejected = $false
try { New-Object FCUAutoDesign.DesignQuantity(1.0, [FCUAutoDesign.DesignUnit]::Unknown) | Out-Null }
catch { $invalidUnitRejected = $true }
Assert-True $invalidUnitRejected 'Unknown unit is rejected'

Assert-True ([Enum]::IsDefined([FCUAutoDesign.DesignErrorCode], 'RevisionMismatch')) 'Revision mismatch error code exists'
Assert-True ([Enum]::IsDefined([FCUAutoDesign.DesignRecordStatus], 'Conflict')) 'Conflict record status exists'

Write-Output "$checks contract checks passed. Revit persistence, transaction, Undo/Redo, and model acceptance are NOT_RUN."
