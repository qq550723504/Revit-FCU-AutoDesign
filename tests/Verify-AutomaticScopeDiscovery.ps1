$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName WindowsBase
$source = Get-Content (Join-Path $PSScriptRoot '..\Geometry\LinearMainCandidateSelector.cs') -Raw -Encoding UTF8
$policy = Get-Content (Join-Path $PSScriptRoot '..\Business\RoomSelection\RoomNameKeywordPolicy.cs') -Raw -Encoding UTF8
$source = [regex]::Replace($source, '(?m)^using .*;\r?$', '')
$policy = [regex]::Replace($policy, '(?m)^using .*;\r?$', '')
$checks = @'
namespace FCUAutoDesign
{
    public static class AutomaticScopeChecks
    {
        static int count;
        static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); count++; Console.WriteLine("PASS: " + name); }
        static LinearMainCandidate Candidate(string id, double x1, double x2, double y = 0)
        { return new LinearMainCandidate { Id = id, Start = new Point3D(x1,y,0), End = new Point3D(x2,y,0) }; }
        public static void Run()
        {
            var rooms = new List<Point3D> { new Point3D(2,3,0), new Point3D(8,3,0) };
            var unique = LinearMainCandidateSelector.Select(new List<LinearMainCandidate> { Candidate("main",0,10) }, rooms, .01);
            Check(unique.UniqueCandidateId == "main", "One main covering all rooms is selected");
            var partial = LinearMainCandidateSelector.Select(new List<LinearMainCandidate> { Candidate("short",0,5) }, rooms, .01);
            Check(partial.UniqueCandidateId == null && partial.EligibleCandidateIds.Count == 0, "Partial branch cannot represent the shared main");
            var ambiguous = LinearMainCandidateSelector.Select(new List<LinearMainCandidate> { Candidate("a",0,10), Candidate("b",0,10,1) }, rooms, .01);
            Check(ambiguous.UniqueCandidateId == null && ambiguous.EligibleCandidateIds.Count == 2, "Multiple covering pipes require user selection");
            var endpoint = LinearMainCandidateSelector.Select(new List<LinearMainCandidate> { Candidate("main",0,10) },
                new List<Point3D> { new Point3D(0,2,0) }, .01);
            Check(endpoint.UniqueCandidateId == null, "Room projection at pipe endpoint is rejected");
            var keywords = Business.RoomSelection.RoomNameKeywordPolicy.Parse("会议室; 办公室，会议室");
            Check(keywords.Count == 2 && Business.RoomSelection.RoomNameKeywordPolicy.Matches("大会议室 01", keywords),
                "Configured room name keywords use contains matching");
            Check(!Business.RoomSelection.RoomNameKeywordPolicy.Matches("卫生间", keywords),
                "Non-target room name is excluded");
            Console.WriteLine(count + " automatic scope checks passed. Revit view discovery is NOT_RUN.");
        }
    }
}
'@
$imports = "using System;`r`nusing System.Collections.Generic;`r`nusing System.Linq;`r`nusing System.Windows.Media.Media3D;`r`n"
$compiler = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\Roslyn\csc.exe'
$windowsBase = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\WindowsBase.dll'
$presentationCore = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\PresentationCore.dll'
$testDir = Join-Path ([IO.Path]::GetTempPath()) ('FCU-AutomaticScope-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testDir)
$testSource = Join-Path $testDir 'Checks.cs'; $exe = Join-Path $testDir 'Checks.exe'
$combined = $imports + $source + [Environment]::NewLine + $policy + [Environment]::NewLine + $checks + [Environment]::NewLine `
    + 'public static class Program { public static void Main() { FCUAutoDesign.AutomaticScopeChecks.Run(); } }'
[IO.File]::WriteAllText($testSource, $combined)
& $compiler /nologo /target:exe "/out:$exe" "/reference:$windowsBase" "/reference:$presentationCore" $testSource
if ($LASTEXITCODE -ne 0) { throw 'Automatic scope harness compilation failed.' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Automatic scope regression failed.' }
