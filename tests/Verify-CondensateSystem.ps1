param([string]$AssemblyPath = (Join-Path $PSScriptRoot '..\bin\CondensateOrthogonalDetour\FCUAutoDesign.dll'))
$ErrorActionPreference = 'Stop'
[void][Reflection.Assembly]::LoadFrom('C:\Program Files\Autodesk\Revit 2020\RevitAPI.dll')
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path $AssemblyPath).Path)
$method = $assembly.GetType('FCUAutoDesign.CondensateSystemPolicy').GetMethod('ConnectorTypeFor')
$count = 0
foreach ($classification in [Enum]::GetValues([Autodesk.Revit.DB.MEPSystemClassification])) {
    $actual = $method.Invoke($null, @($classification))
    $name = $classification.ToString()
    if ($name -eq 'Sanitary') {
        if ($null -eq $actual -or $actual.ToString() -ne $name) { throw "FAIL: $name must map to the same connector classification" }
    } elseif ($null -ne $actual) { throw "FAIL: $name must not be accepted as condensate" }
    $count++
}
$candidate = $assembly.GetType('FCUAutoDesign.CondensateSystemPolicy').GetMethod('IsCandidate')
foreach ($expected in [Enum]::GetValues([Autodesk.Revit.DB.Plumbing.PipeSystemType])) {
    foreach ($actual in [Enum]::GetValues([Autodesk.Revit.DB.Plumbing.PipeSystemType])) {
        $want = $expected.ToString() -eq 'Sanitary' -and $actual.ToString() -eq 'Sanitary'
        $got = [bool]$candidate.Invoke($null, @($actual, $expected))
        if ($got -ne $want) { throw "FAIL: connector $actual to target $expected" }
        $count++
    }
}
Write-Output "$count classification checks passed. Revit model connection tests are NOT_RUN."
