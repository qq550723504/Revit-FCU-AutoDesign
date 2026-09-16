param([string]$AssemblyPath = (Join-Path $PSScriptRoot '..\bin\CondensateOrthogonalDetour\FCUAutoDesign.dll'))

# In-process WPF regression test; does not automate or modify a running Revit instance.
# Run with Windows PowerShell: powershell.exe -NoProfile -STA -File tests\Verify-Dialog.ps1
$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run with -STA.' }
Add-Type -AssemblyName PresentationFramework
[void][Reflection.Assembly]::LoadFrom((Resolve-Path $AssemblyPath).Path)
$readParameters = [FCUAutoDesign.FCUDesignWindow].GetMethod('TryReadParameters', [Reflection.BindingFlags]'NonPublic,Instance')
$script:checks = 0

function Assert-True([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "FAIL: $Name" }
    $script:checks++
    Write-Output "PASS: $Name"
}

function New-TestWindow {
    $window = New-Object FCUAutoDesign.FCUDesignWindow
    $types = New-Object 'System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[int,string]]'
    $types.Add((New-Object 'System.Collections.Generic.KeyValuePair[int,string]' 101,'Test FCU'))
    $window.SetFcuTypes($types)
    $window.ShowInTaskbar = $false
    $window.ShowActivated = $false
    $window.Opacity = 0
    return $window
}

foreach ($action in 'BtnRun','BtnCancel','Close') {
    $window = New-TestWindow
    $window.FindName('ChkMultipleFcus').IsChecked = $false
    $callback = {
        if ($action -eq 'Close') { $this.Close(); return }
        $this.FindName($action).RaiseEvent((New-Object System.Windows.RoutedEventArgs([System.Windows.Controls.Button]::ClickEvent)))
    }.GetNewClosure()
    $window.Add_ContentRendered($callback)
    $result = $window.ShowDialog()
    $expected = $action -eq 'BtnRun'
    Assert-True ($result -eq $expected -and $window.IsConfirmed -eq $expected) "Modal action $action returns the correct command outcome"
    if ($expected) { Assert-True (-not $window.EnableMultipleFcus) 'Unchecked multi-FCU option reaches confirmed parameters' }
}

$window = New-TestWindow
try {
    Assert-True ([bool]$readParameters.Invoke($window, @())) 'Default parameters accepted'
    Assert-True ($window.FindName('ChkMultipleFcus').IsChecked -eq $true -and $window.EnableMultipleFcus) 'Multi-FCU placement enabled by default'
    Assert-True ($window.FindName('ChkAutomaticScope').IsChecked -eq $false -and -not $window.EnableAutomaticScopeDiscovery) 'Automatic scope discovery is explicit opt-in'
    Assert-True ($null -eq $window.FindName('TxtCondensateSlope')) 'No condensate slope input exists'
    $window.FindName('ChkEnableCondensate').IsChecked = $true
    Assert-True ([bool]$readParameters.Invoke($window, @())) 'Enabled condensate needs no slope setting'
    $window.FindName('ChkEnableCondensate').IsChecked = $false
    $window.FindName('FcuTypePicker').SelectedIndex = -1
    Assert-True (-not [bool]$readParameters.Invoke($window, @())) 'Missing FCU selection rejected'
    $window.FindName('FcuTypePicker').SelectedIndex = 0
    foreach ($bad in '', 'abc', '-10', '0', 'NaN', 'Infinity', '1e999') {
        $window.FindName('TxtDoorOffset').Text = $bad
        Assert-True (-not [bool]$readParameters.Invoke($window, @())) "Invalid distance rejected: [$bad]"
        Assert-True ($window.DoorOffsetMm -eq 500) 'Rejected input does not overwrite accepted parameters'
    }
    $window.FindName('TxtDoorOffset').Text = '900'
    $window.FindName('ChkEnableCondensate').IsChecked = $true
    Assert-True ([bool]$readParameters.Invoke($window, @())) 'Custom parameters accepted without slope setting'
    Assert-True ($window.DoorOffsetMm -eq 900) 'Custom distance preserved'
    Assert-True ($null -eq $window.MinimumStraightLengthMm) 'No fabricated engineering minimum'
    Assert-True ($null -eq $window.FindName('ChkCenterPlacement')) 'Geometry-center option removed'
    foreach ($bad in '0', '-1', 'NaN', 'Infinity', '400', '450', 'abc') {
        $window.FindName('TxtMinimumStraight').Text = $bad
        Assert-True (-not [bool]$readParameters.Invoke($window, @())) "Invalid net minimum rejected: $bad"
        Assert-True ($null -eq $window.MinimumStraightLengthMm) 'Rejected minimum does not overwrite state'
    }
    $window.FindName('TxtMinimumStraight').Text = '200'
    Assert-True ([bool]$readParameters.Invoke($window, @())) 'Explicit installation minimum accepted'
    Assert-True ($window.MinimumStraightLengthMm -eq 200) 'Installation minimum retained'
    $window.FindName('TxtMinimumStraight').Text = ''
    Assert-True ([bool]$readParameters.Invoke($window, @())) 'Minimum may be cleared'
    Assert-True ($null -eq $window.MinimumStraightLengthMm) 'Cleared minimum is unknown, not zero'
} finally { $window.Close() }
Write-Output "$script:checks checks passed. Revit geometry/connection tests are NOT_RUN by this script."
