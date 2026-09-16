# Compile the production result models with inert Revit ID/XYZ stand-ins.
# This verifies result handoff only; it does not exercise Revit geometry or transactions.
$ErrorActionPreference = 'Stop'
$models = @('Models\TeeConnectionResult.cs', 'Models\CondensateDrainResult.cs') | ForEach-Object {
    $text = Get-Content (Join-Path $PSScriptRoot ('..\' + $_)) -Raw -Encoding UTF8
    [regex]::Replace($text, '(?m)^using .*;\r?$', '')
}
$harness = @'
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
namespace Autodesk.Revit.DB
{
    public sealed class ElementId { public int Value; public ElementId(int value) { Value = value; } }
    public sealed class XYZ { }
}
namespace FCUAutoDesign
{
    public static class ConnectionResultChecks
    {
        public static void Main() { Run(); }
        static int count;
        static void Check(bool ok, string message)
        {
            if (!ok) throw new Exception("FAIL: " + message);
            count++; Console.WriteLine("PASS: " + message);
        }
        public static void Run()
        {
            var source = new TeeConnectionResult {
                BranchCreated = true, TeeCreated = true, FcuConnectorId = 3,
                FirstPipeId = new ElementId(21), MinimumStraightLength = .5,
                MainPart1Id = new ElementId(31), MainPart2Id = new ElementId(32),
                MainPart1AdapterId = new ElementId(41), MainPart2AdapterId = new ElementId(42),
                ErrorMessage = "diagnostic"
            };
            source.Chain.Add(new ElementId(20)); source.Chain.Add(source.FirstPipeId);
            source.ElementRoles[21] = "outlet pipe";
            var drain = new CondensateDrainResult(source);
            Check(Object.ReferenceEquals(drain.Connection, source), "Verified connection is handed off intact, not field-copied");
            Check(drain.Connection.FirstPipeId.Value == 21, "Post-commit verification retains first pipe ID");
            Check(drain.Connection.MinimumStraightLength == .5, "Explicit installation minimum survives handoff");
            Check(drain.Connection.Chain.Count == 2 && drain.Connection.ElementRoles[21] == "outlet pipe", "Connection chain and diagnostics survive handoff");
            Check(drain.Connection.MainPart1Id.Value == 31 && drain.Connection.MainPart2Id.Value == 32
                && drain.Connection.MainPart1AdapterId.Value == 41 && drain.Connection.MainPart2AdapterId.Value == 42,
                "Split mains and transitions survive handoff");
            Check(drain.Connected && drain.ErrorMessage == "diagnostic", "Successful result exposes connection state and message");
            var unknown = new CondensateDrainResult(new TeeConnectionResult { BranchCreated = true, TeeCreated = true, FirstPipeId = new ElementId(51) });
            Check(unknown.Connection.FirstPipeId.Value == 51 && !unknown.Connection.MinimumStraightLength.HasValue,
                "Unknown minimum remains unknown without losing first pipe ID");
            Check(!new CondensateDrainResult(new TeeConnectionResult { BranchCreated = true }).Connected,
                "Unconnected tee is not reported as connected");
            Check(!new CondensateDrainResult(new TeeConnectionResult { TeeCreated = true }).Connected,
                "Missing branch is not reported as connected");
            var failed = new CondensateDrainResult();
            Check(!failed.Connected && failed.Connection != null && !failed.Connection.BranchCreated,
                "Failure result retains a valid empty record");
            bool rejected = false;
            try { new CondensateDrainResult(null); } catch (ArgumentNullException) { rejected = true; }
            Check(rejected, "Null explicit connection record rejected");
            Console.WriteLine(count + " connection result checks passed. Revit geometry/commit/rollback: NOT_RUN.");
        }
    }
}
'@
$compiler = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\Roslyn\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Visual Studio Roslyn compiler not found.' }
$testDir = Join-Path ([IO.Path]::GetTempPath()) ('FCU-ConnectionResult-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testDir)
$sourcePath = Join-Path $testDir 'Checks.cs'
$exePath = Join-Path $testDir 'Checks.exe'
[IO.File]::WriteAllText($sourcePath, ($harness + [Environment]::NewLine + ($models -join [Environment]::NewLine)))
& $compiler /nologo /target:exe "/out:$exePath" $sourcePath
if ($LASTEXITCODE -ne 0) { throw 'Regression harness compilation failed.' }
& $exePath
if ($LASTEXITCODE -ne 0) { throw 'Connection result regression failed.' }
