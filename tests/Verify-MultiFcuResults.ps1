$ErrorActionPreference = 'Stop'
# Production result aggregation and batch registration with inert Revit stand-ins.
# Does not execute Revit geometry or transactions.
$sources = @('Models\FcuDesignResult.cs', 'Models\ExecutionOutcome.cs',
    'Models\RoomExecutionResult.cs', 'Services\RoomBatchContext.cs') | ForEach-Object {
    $source = Get-Content (Join-Path $PSScriptRoot ('..\' + $_)) -Raw -Encoding UTF8
    [regex]::Replace($source, '(?m)^using .*;\r?$', '')
}
$harness = @'
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
namespace Autodesk.Revit.DB { public class ElementId {} public class XYZ {} public class Document {} }
namespace Autodesk.Revit.DB.Plumbing { public class Pipe {} }
namespace FCUAutoDesign {
    internal class TeeConnectionResult {}
    internal class FcuDesignOptions { public bool EnableReturnPipe; public bool EnableCondensate; }
    internal class MainPipeRun {
        public List<TeeConnectionResult> Items = new List<TeeConnectionResult>();
        public MainPipeRun(Pipe p) {}
        public MainPipeRun Fork() { var copy = new MainPipeRun(null); copy.Items.AddRange(Items); return copy; }
        public void Register(TeeConnectionResult c) { if (c != null) Items.Add(c); }
        public void VerifyPrevious(Document d, TeeConnectionResult p, TeeConnectionResult c) {}
    }
    internal static class CircuitInterferenceVerifier {
        public static void Verify(Document d, TeeConnectionResult a, TeeConnectionResult b, string x, string y, bool z) {}
    }
    public static class MultiFcuChecks {
        public static void Main() { Run(); }
        static int count;
        static void Check(bool value, string name) {
            if (!value) throw new Exception("FAIL: " + name);
            count++; Console.WriteLine("PASS: " + name);
        }
        static FcuDesignResult Unit() {
            return new FcuDesignResult {
                Outcome = new ExecutionOutcome { SupplyTeeConnected = true, ReturnTeeConnected = true, CondensateConnected = true },
                SupplyConnection = new TeeConnectionResult(), ReturnConnection = new TeeConnectionResult(), DrainConnection = new TeeConnectionResult()
            };
        }
        public static void Run() {
            var options = new FcuDesignOptions { EnableReturnPipe = true, EnableCondensate = true };
            var one = Unit(); var two = Unit();
            Check(one.Devices.Single() == one, "Single-device results retain their device identity");
            var aggregate = new FcuDesignResult(); aggregate.UnitResults.Add(one); aggregate.UnitResults.Add(two);
            Check(aggregate.Devices.SequenceEqual(new[] { one, two }), "Multi-device report includes every device in order");
            var room = new RoomExecutionResult { Design = aggregate };
            Check(room.Status(options) == "连接完成", "All enabled connections completed");
            two.Outcome.CondensateConnected = false;
            Check(room.Status(options) == "部分完成", "Second-device failure cannot be hidden by first-device success");
            options.EnableCondensate = false;
            Check(room.Status(options) == "连接完成", "Disabled condensate does not mark the room incomplete");
            two.Outcome.ReturnTeeConnected = false;
            Check(room.Status(options) == "部分完成", "Enabled return is checked on every device");
            room.Design = null;
            Check(room.Status(options) == "失败（已回滚）", "Rolled-back room exposes no successful device result");
            room.NotRun = true;
            Check(room.Status(options) == "未执行", "Unexecuted room remains distinct from rollback");
            var parent = new RoomBatchContext(new Pipe(), new Pipe(), new Pipe());
            parent.Register(Unit());
            var working = parent.Fork(); working.Register(aggregate);
            Check(parent.Supply.Items.Count == 1 && parent.Return.Items.Count == 1 && parent.Condensate.Items.Count == 1,
                "Room-local registration cannot pollute parent state before commit");
            Check(working.Supply.Items.Count == 3 && working.Return.Items.Count == 3 && working.Condensate.Items.Count == 3,
                "Room-local context includes earlier rooms and both new devices");
            parent.Register(aggregate);
            Check(parent.Supply.Items.Count == 3 && parent.Return.Items.Count == 3 && parent.Condensate.Items.Count == 3,
                "Committed room registers every device circuit");
            Console.WriteLine(count + " multi-FCU result/context checks passed. Revit geometry and rollback acceptance: NOT_RUN.");
        }
    }
}
'@
$compiler = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\Roslyn\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Visual Studio Roslyn compiler not found.' }
$testDir = Join-Path ([IO.Path]::GetTempPath()) ('FCU-MultiFcu-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testDir)
$sourcePath = Join-Path $testDir 'Checks.cs'
$exePath = Join-Path $testDir 'Checks.exe'
[IO.File]::WriteAllText($sourcePath, ($harness + [Environment]::NewLine + ($sources -join [Environment]::NewLine)))
& $compiler /nologo /target:exe "/out:$exePath" $sourcePath
if ($LASTEXITCODE -ne 0) { throw 'Multi-FCU harness compilation failed.' }
& $exePath
if ($LASTEXITCODE -ne 0) { throw 'Multi-FCU regression failed.' }
