# Same-room multi-FCU trial

Target: Revit 2020, .NET Framework 4.8.

The dialog enables multi-FCU placement by default. Disable it to retain the single-unit path.
The count is ceil(L/5), for 0 < L <= 40 m. L follows the selected door wall.
Each unit is at (2i-1)L/(2n), with a default 500 mm offset from the door-side wall interior.
All units use the explicitly selected family type. Catalog-to-family capacity matching is not implemented.
Existing PoC branch sizing remains in use; this is not formal hydraulic sizing.

## Local checks

Run in Windows PowerShell from the repository root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\Verify-EquipmentSelection.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\Verify-MultiFcuResults.ps1
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' FCUAutoDesign.csproj /t:Rebuild /p:Configuration=Release /p:Platform=x64 /p:RevitVersion=2020 /nologo /verbosity:minimal
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File tests\Verify-Dialog.ps1 -AssemblyPath bin\Release\FCUAutoDesign.dll
```

Verified outputs: 41 equipment-selection checks, 11 result/context checks, 45 dialog checks; Release build succeeded.
Result/context tests use inert Revit stand-ins. They prove aggregation and private registration, not model rollback.

## Revit acceptance — NOT_RUN

Use a model copy with known family, pipe and fitting configurations. Record the source commit, DLL hash and element IDs.

- L <= 5 m: one unit at L/2; existing single-device routing behavior retained.
- 5 < L <= 10 m: two units at L/4 and 3L/4; both devices have all enabled connections.
- 10 < L <= 15 m: three units at L/6, L/2 and 5L/6.
- Rotated/reversed door wall and same-edge multiple doors: positions follow local wall axes and all remain inside the room.
- Second/third unit splits a main already split by the first: all earlier connections remain valid.
- Force a later unit to fail: whole current room rolls back, earlier completed rooms remain intact, no failed-room segment IDs enter the parent batch.
- Multi-room batch: reports include each FCU ID and circuit status; no first-device-only success report.
- After all units connect: final placement, outlet direction, installation clearance and earlier connection chains pass checks.
- Verify Undo/Redo in Revit. Repeated execution/idempotent recalculation is not delivered by this change.

Geometry checks use placement points, not the full device envelope. Engineering capacity, insulation, maintenance space and drainage slope still require separate acceptance.
