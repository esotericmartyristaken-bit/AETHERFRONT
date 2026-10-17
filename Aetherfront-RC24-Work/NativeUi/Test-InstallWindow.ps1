#requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop' -or [Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    throw 'Run this test with Windows PowerShell 5.1 -STA. No visible window or installation is started.'
}
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$work = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
foreach ($path in @('Patched/ApfCore.dll', 'Engine/bin/Release/net472/KesasInstall.dll', 'NativeUi/bin/Release/net472/AetherfrontInstallUI.dll')) {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $work $path))
}
$flags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$windowType = [AetherfrontInstallUI.InstallWindow].GetNestedType('ProgressWindow', [Reflection.BindingFlags]::NonPublic)
$windowConstructor = $windowType.GetConstructor($flags, $null, [type[]]@([string], [string], [string]), $null)
$completionType = [KesasInstall.NativeInstall].GetNestedType('Completion', [Reflection.BindingFlags]::Public)
$completionConstructors = @($completionType.GetConstructors($flags) | Where-Object {
    $parameters = $_.GetParameters()
    $parameters.Count -eq 2 -and $parameters[1].ParameterType -eq [string]
})
if ($completionConstructors.Count -ne 1) { throw 'Expected Completion(transaction, root) constructor.' }
$taskSourceType = [System.Threading.Tasks.TaskCompletionSource``1].MakeGenericType($completionType)
$script:checks = 0

function Assert-Ui([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
    $script:checks++
}
function New-TestWindow([string]$Kind) {
    # OnShown is deliberately never called: no NativeInstall.Run, timer, or visible window.
    return $windowConstructor.Invoke([string[]]@($work, (Join-Path $work 'unused-transaction.json'), $Kind))
}
function Get-WindowField($Window, [string]$Name) {
    return $windowType.GetField($Name, $flags).GetValue($Window)
}
function Set-Worker($Window, $Task) {
    $windowType.GetField('worker', $flags).SetValue($Window, $Task)
}
function Invoke-WindowMethod($Window, [string]$Name, [object[]]$Arguments) {
    for ($index = 0; $index -lt $Arguments.Length; $index++) {
        if ($null -ne $Arguments[$index]) { $Arguments[$index] = $Arguments[$index].PSObject.BaseObject }
    }
    [void]$windowType.GetMethod($Name, $flags).Invoke($Window, $Arguments)
}
function Assert-CanClose($Window, [bool]$ExpectedCancellation) {
    $closing = New-Object System.Windows.Forms.FormClosingEventArgs([System.Windows.Forms.CloseReason]::UserClosing, $false)
    Invoke-WindowMethod $Window 'OnFormClosing' ([object[]]@($closing))
    Assert-Ui ($closing.Cancel -eq $ExpectedCancellation) 'Unexpected close cancellation state.'
}

$window = New-TestWindow 'pack'
try {
    Assert-Ui (!(Get-WindowField $window 'restart').Enabled) 'Restart must initially be disabled.'
    Assert-Ui (!(Get-WindowField $window 'close').Enabled) 'Close must initially be disabled.'
    Assert-CanClose $window $true
    Assert-Ui (!(Get-WindowField $window 'worker')) 'Constructor must not start installation.'
} finally { $window.Dispose() }

$window = New-TestWindow 'pack'
try {
    $source = [Activator]::CreateInstance($taskSourceType)
    $detail = 'Fixture root failure: ' + ('x' * 900)
    $inner = New-Object System.InvalidOperationException($detail)
    $fixtureError = New-Object System.InvalidOperationException('Fixture wrapper', $inner)
    $source.SetException([Exception]$fixtureError)
    Set-Worker $window $source.Task
    Invoke-WindowMethod $window 'Poll' ([object[]]@($null, [EventArgs]::Empty))
    Assert-Ui ($window.ExitCode -eq 1) 'Failed installation must return failure.'
    Assert-Ui (!(Get-WindowField $window 'restart').Enabled) 'Failure must disable restart.'
    Assert-Ui ((Get-WindowField $window 'close').Enabled) 'Failure must permit close.'
    $text = (Get-WindowField $window 'status').Text
    Assert-Ui ($text.Contains('Fixture root failure: ') -and !$text.Contains('Fixture wrapper')) 'Failure must show the base exception message.'
    Assert-Ui ($text.Length -lt 800 -and $text.Contains('APF/APFUpdater.log')) 'Failure details must be concise and identify the log.'
    Assert-CanClose $window $false
} finally { $window.Dispose() }

foreach ($kind in @('rollback', 'pack', 'client')) {
    $window = New-TestWindow $kind
    try {
        $completion = $completionConstructors[0].Invoke([object[]]@($null, [string]$work))
        $source = [Activator]::CreateInstance($taskSourceType)
        $source.SetResult($completion)
        Set-Worker $window $source.Task
        Invoke-WindowMethod $window 'Poll' ([object[]]@($null, [EventArgs]::Empty))
        Assert-Ui ($window.ExitCode -eq 0) 'Completed installation must return success.'
        Assert-Ui ((Get-WindowField $window 'close').Enabled) 'Completed installation must permit close.'
        Assert-Ui (!(Get-WindowField $window 'restartWorker')) 'Success must never automatically restart KSP.'
        if ($kind -eq 'rollback') {
            Assert-Ui ((Get-WindowField $window 'heading').Text -eq 'Restore complete') 'Rollback must display Restore complete.'
            Assert-Ui (!(Get-WindowField $window 'restart').Enabled) 'Rollback must not enable restart.'
            Assert-Ui ($window.AcceptButton -eq (Get-WindowField $window 'close')) 'Rollback default action must be Close.'
            Invoke-WindowMethod $window 'RestartClicked' ([object[]]@($null, [EventArgs]::Empty))
            Assert-Ui (!(Get-WindowField $window 'restartWorker')) 'Direct rollback restart handler must remain blocked.'
        } else {
            Assert-Ui ((Get-WindowField $window 'restart').Enabled) 'Verified pack/client installation must enable explicit restart.'
        }
        Assert-CanClose $window $false
        Assert-Ui (!$window.Visible) 'Reflection tests must not display a window.'
    } finally {
        $window.Dispose()
        $window.Dispose()
    }
}
Write-Output "PASS $script:checks hidden STA UI checks; no NativeInstall.Run, installation, or KSP restart."
