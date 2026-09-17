param([string]$AssemblyPath = (Join-Path $PSScriptRoot '..\bin\Release\FCUAutoDesign.dll'))

$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path $AssemblyPath).Path)
$storeType = $assembly.GetType('FCUAutoDesign.Agent.AgentSettingsStore', $true)
$configurationType = $assembly.GetType('FCUAutoDesign.Agent.AgentConfiguration', $true)
$constructor = $storeType.GetConstructor(
    [Reflection.BindingFlags]'NonPublic,Instance', $null, [Type[]]@([string]), $null)
if ($null -eq $constructor) { throw 'FAIL: testable settings-store constructor not found' }

$testDirectory = Join-Path ([IO.Path]::GetTempPath()) ('FCU-AgentSettings-' + [Guid]::NewGuid().ToString('N'))
$settingsPath = Join-Path $testDirectory 'agent-settings.json'
[void][IO.Directory]::CreateDirectory($testDirectory)
$checks = 0
function Assert-True([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "FAIL: $Name" }
    $script:checks++
    Write-Output "PASS: $Name"
}

$store = $constructor.Invoke([object[]]@($settingsPath.PSObject.BaseObject))
$secret = 'test-secret-that-must-not-appear-in-the-file'
try {
    $configuration = [Activator]::CreateInstance($configurationType)
    $configuration.BaseUrl = 'https://agent.example/v1'
    $configuration.Model = 'compatible-model'
    $configuration.ApiKey = $secret
    $store.Save($configuration)

    Assert-True (Test-Path -LiteralPath $settingsPath) 'AI settings are persisted to the selected local path'
    $storedText = [IO.File]::ReadAllText($settingsPath)
    Assert-True (-not $storedText.Contains($secret)) 'API key is not stored as plaintext'
    Assert-True ($storedText.Contains('encrypted_api_key')) 'Persisted settings carry encrypted key material'

    $loaded = $store.Load()
    Assert-True ($loaded.IsSavedLocally) 'Saved local settings take precedence over environment fallback'
    Assert-True ($loaded.Configuration.BaseUrl -eq 'https://agent.example/v1' -and $loaded.Configuration.Model -eq 'compatible-model') 'Endpoint and model round-trip'
    Assert-True ($loaded.Configuration.ApiKey -eq $secret) 'DPAPI key round-trips for the current Windows user'

    $store.Delete()
    Assert-True (-not (Test-Path -LiteralPath $settingsPath)) 'Saved local settings can be removed'

    [IO.File]::WriteAllText($settingsPath, '{invalid json')
    $fallback = $store.Load()
    Assert-True (-not $fallback.IsSavedLocally -and -not [string]::IsNullOrWhiteSpace($fallback.Warning)) 'Corrupt settings fail closed to the established fallback with a visible warning'
}
finally {
    if (Test-Path -LiteralPath $settingsPath) { Remove-Item -LiteralPath $settingsPath -Force }
    if (Test-Path -LiteralPath $testDirectory) { Remove-Item -LiteralPath $testDirectory -Force }
}
Write-Output "$checks AI settings checks passed. No real API request was sent."
