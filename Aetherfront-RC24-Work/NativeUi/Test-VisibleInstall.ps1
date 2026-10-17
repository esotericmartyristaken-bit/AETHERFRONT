#requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop' -or [Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    throw 'Run this owned component test with Windows PowerShell 5.1 -STA.'
}
$clock = [Diagnostics.Stopwatch]::StartNew()
$work = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixture = [IO.Path]::GetFullPath((Join-Path $work 'NativeFixture-454c4efb6d0e4bc5b4a2c66bd6d26646'))
$stage = $null
$window = $null
$checks = 0
$utf8 = New-Object Text.UTF8Encoding($false)

function Assert-VisibleTest([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
    $script:checks++
}
function Assert-WithinTime {
    if ($clock.Elapsed.TotalSeconds -ge 120) { throw 'Owned visible installation test exceeded its 120 second bound.' }
}
function Wait-UiUntil([scriptblock]$Condition) {
    while (!(& $Condition)) {
        Assert-WithinTime
        [Windows.Forms.Application]::DoEvents()
        [Threading.Thread]::Sleep(20)
    }
    Assert-WithinTime
}

try {
    Assert-VisibleTest ($fixture.StartsWith($work + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) 'Fixture must remain inside RC24 work.'
    Assert-VisibleTest ((New-Object IO.DriveInfo([IO.Path]::GetPathRoot($fixture))).DriveType -eq [IO.DriveType]::Fixed) 'Fixture must use a local fixed disk.'
    foreach ($relative in @('../Aetherfront-RC23-Work/Candidate/Client/GameData/LunaMultiplayer/Plugins/JsonFx.dll', 'Patched/ApfCore.dll', 'Engine/bin/Release/net472/KesasInstall.dll', 'NativeUi/bin/Release/net472/AetherfrontInstallUI.dll')) {
        [void][Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath((Join-Path $work $relative)))
    }
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [ApfCore.ApfPaths]::EnsureNoReparsePoints($work, $fixture)
    $proof = Get-Content -LiteralPath (Join-Path $fixture 'result.json') -Raw | ConvertFrom-Json
    Assert-VisibleTest ($proof.passed -and $proof.checks -eq 14) 'Expected the completed original native fixture.'
    $helperRoot = Join-Path $fixture 'GameData/LunaMultiplayer'
    $example = Join-Path $fixture 'GameData/TestCore/example.cfg'
    Assert-VisibleTest ([IO.File]::ReadAllText($example) -ceq 'native PowerShell helper content') 'Expected Test-Helper.ps1 completed baseline; do not reuse this test transaction.'
    $stub = [IO.Path]::GetFullPath((Join-Path $work '../Aetherfront-RC22-Work/GameStub/bin/Release/net472/GameStub.exe'))
    $stubHash = [ApfCore.ApfJson]::Sha256File($stub)
    foreach ($name in @('KSP_x64.exe', 'KSP.exe')) {
        $target = Join-Path $fixture $name
        [ApfCore.ApfPaths]::EnsureNoReparsePoints($fixture, $target)
        Assert-VisibleTest ([ApfCore.ApfJson]::Sha256File($target) -ceq $stubHash) 'Restart target must be the known empty GameStub, never a real KSP binary.'
    }
    $preserved = @{}
    foreach ($relative in @('saves/Tester/persistent.sfs', 'GameData/LunaMultiplayer/Data/privatekey.txt')) {
        $preserved[$relative] = [ApfCore.ApfJson]::Sha256File((Join-Path $fixture $relative))
    }
    $resultPath = Join-Path $fixture 'APF/LastInstallResult.json'
    $previousResult = [IO.File]::ReadAllText($resultPath)
    $previousReceipt = $previousResult | ConvertFrom-Json
    Assert-VisibleTest ($previousReceipt.status -ceq 'complete') 'Expected the previous native helper success receipt.'

    $testId = [Guid]::NewGuid().ToString('N')
    $stage = Join-Path $fixture ('APF/.staging/visible-ui-' + $testId)
    [ApfCore.ApfPaths]::EnsureNoReparsePoints($fixture, $stage)
    Assert-VisibleTest (!(Test-Path -LiteralPath $stage)) 'A fresh unique staging directory is required.'
    [void][IO.Directory]::CreateDirectory($stage)
    $archive = Join-Path $stage 'fixture.zip'
    $expectedContent = 'visible native UI verified content ' + $testId
    $zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $zip.CreateEntry('TestCore/example.cfg')
        $writer = New-Object IO.StreamWriter($entry.Open())
        try { $writer.Write($expectedContent) } finally { $writer.Dispose() }
    } finally { $zip.Dispose() }
    $package = New-Object ApfCore.ApfPackage
    $package.Id = 'testcore'
    $package.Version = 'visible-ui-' + $testId
    $package.ArchiveUrl = 'https://217.216.49.250:18866/fixture.zip'
    $package.Sha256 = [ApfCore.ApfJson]::Sha256File($archive)
    $package.SizeBytes = ([IO.FileInfo]$archive).Length
    $package.Destination = 'GameData'
    $package.StripPrefix = 'TestCore'
    $manifest = New-Object ApfCore.ApfPackManifest
    $manifest.ServerId = 'kesas-b6286df413b9426f'
    $manifest.PackId = 'kesas'
    $manifest.PackVersion = 'kesas-rc24'
    $manifest.RequiredClientVersion = 'kesas-rc24'
    $manifest.KspVersion = '1.12.5'
    foreach ($name in @('Squad', 'SquadExpansion', 'LunaMultiplayer', '000_Harmony')) { $manifest.ProtectedRoots.Add($name) }
    $manifest.ManagedPackages.Add($package)
    $transaction = New-Object ApfCore.ApfSyncTransaction
    $transaction.GameRoot = $fixture
    $transaction.GameDataPath = Join-Path $fixture 'GameData'
    $transaction.KspExecutable = Join-Path $fixture 'KSP_x64.exe'
    $transaction.ProcessId = 0
    $transaction.KspArguments.Add('--visible-native-ui-' + $testId)
    $transaction.StatePath = Join-Path $helperRoot 'Data/APFPackState.json'
    $transaction.Manifest = $manifest
    $staged = New-Object ApfCore.ApfStagedPackage
    $staged.Package = $package
    $staged.ArchivePath = $archive
    $transaction.Packages.Add($staged)
    $key = [ApfCore.ApfTransactionSecurity]::LoadKey((Join-Path $helperRoot 'Data/APFHelper.key'))
    [ApfCore.ApfTransactionSecurity]::Sign($transaction, $key)
    Assert-VisibleTest ([ApfCore.ApfTransactionSecurity]::Verify($transaction, $key)) 'Fresh transaction signature must verify.'
    $request = Join-Path $stage 'transaction.json'
    [IO.File]::WriteAllText($request, [ApfCore.ApfJson]::Serialize($transaction), $utf8)

    [Windows.Forms.Application]::EnableVisualStyles()
    $flags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
    $windowType = [AetherfrontInstallUI.InstallWindow].GetNestedType('ProgressWindow', [Reflection.BindingFlags]::NonPublic)
    $constructor = $windowType.GetConstructor($flags, $null, [type[]]@([string], [string], [string]), $null)
    $window = $constructor.Invoke([string[]]@($helperRoot, $request, 'pack'))
    $restart = $windowType.GetField('restart', $flags).GetValue($window)
    Assert-VisibleTest (!$restart.Enabled) 'Restart must be disabled before showing the form.'
    Assert-WithinTime
    $window.Show()
    Assert-VisibleTest $window.Visible 'The real progress form must be visible.'
    Assert-VisibleTest ([bool]$windowType.GetField('busy', $flags).GetValue($window)) 'Shown form must be busy while applying.'
    Assert-VisibleTest (!$restart.Enabled) 'Restart must remain disabled when installation starts.'
    $closing = New-Object Windows.Forms.FormClosingEventArgs([Windows.Forms.CloseReason]::UserClosing, $false)
    [void]$windowType.GetMethod('OnFormClosing', $flags).Invoke($window, [object[]]@($closing.PSObject.BaseObject))
    Assert-VisibleTest $closing.Cancel 'Closing must be canceled while the actual worker is active.'
    $window.Close()
    Assert-VisibleTest ($window.Visible -and !$window.IsDisposed) 'An actual close attempt must leave the busy form open.'

    Wait-UiUntil { $window.IsDisposed -or ![bool]$windowType.GetField('busy', $flags).GetValue($window) }
    Assert-VisibleTest (!$window.IsDisposed -and $window.Visible) 'Verified success must leave the progress form visible.'
    $statusText = $windowType.GetField('status', $flags).GetValue($window).Text
    Assert-VisibleTest ($window.ExitCode -eq 0) ('Actual native worker failed: ' + $statusText)
    Assert-VisibleTest ($restart.Enabled -and [KesasInstall.NativeInstall]::Status.Contains('verified')) 'Restart must become enabled only after actual verification.'
    Assert-VisibleTest ([IO.File]::ReadAllText($example) -ceq $expectedContent) 'Actual native worker must install the fresh archive content.'
    $completion = $windowType.GetField('completion', $flags).GetValue($window)
    Assert-VisibleTest ($null -ne $completion -and $completion.RestartedProcessId -eq 0) 'A completion must exist with no automatic restart.'
    $currentResult = [IO.File]::ReadAllText($resultPath)
    $receipt = $currentResult | ConvertFrom-Json
    Assert-VisibleTest ($receipt.status -ceq 'complete' -and $receipt.kind -ceq 'pack' -and $currentResult -cne $previousResult) 'A new complete pack receipt is required.'
    Assert-VisibleTest ([DateTime]::Parse($receipt.updatedUtc).ToUniversalTime() -gt [DateTime]::Parse($previousReceipt.updatedUtc).ToUniversalTime()) 'The completion receipt must be newer than the previous helper receipt.'

    [Windows.Forms.Application]::DoEvents()
    $screenshot = Join-Path $stage 'visible-completion.png'
    $bitmap = New-Object Drawing.Bitmap($window.Width, $window.Height)
    try {
        $window.DrawToBitmap($bitmap, (New-Object Drawing.Rectangle(0, 0, $window.Width, $window.Height)))
        $bitmap.Save($screenshot, [Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }
    $restart.PerformClick()
    Assert-VisibleTest (!$restart.Enabled) 'The actual Restart click must disable repeated requests.'
    Wait-UiUntil { $window.IsDisposed -or !$window.Visible -or $window.ExitCode -ne 0 }
    Assert-VisibleTest ($window.IsDisposed -and $window.ExitCode -eq 0) ('Explicit restart must close the form successfully: ' + $windowType.GetField('status', $flags).GetValue($window).Text)
    Assert-VisibleTest ($completion.RestartedProcessId -gt 0) 'The actual Restart button must launch the owned empty GameStub.'
    $finalReceipt = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    Assert-VisibleTest ($finalReceipt.status -ceq 'complete' -and $finalReceipt.updatedUtc -ceq $receipt.updatedUtc) 'Restart must retain this new verified completion receipt.'
    foreach ($relative in $preserved.Keys) {
        Assert-VisibleTest ([ApfCore.ApfJson]::Sha256File((Join-Path $fixture $relative)) -ceq $preserved[$relative]) 'Fixture save and identity must be preserved.'
    }
    $report = [ordered]@{
        passed = $true; checks = $checks; visibleUi = $true; actualNativeWorker = $true; explicitRestart = $true
        fixture = $fixture; request = $request; screenshot = $screenshot
        restartedProcessId = $completion.RestartedProcessId; progressExitCode = $window.ExitCode
        receipt = $finalReceipt; elapsedSeconds = $clock.Elapsed.TotalSeconds
        completedUtc = [DateTime]::UtcNow.ToString('o'); unityGameplayTested = $false
    }
    [IO.File]::WriteAllText((Join-Path $stage 'visible-result.json'), ($report | ConvertTo-Json -Depth 8), $utf8)
    Write-Output ('PASS visible native UI checks=' + $checks + ' elapsedSeconds=' + $clock.Elapsed.TotalSeconds.ToString('0.00') + ' restartedProcessId=' + $completion.RestartedProcessId)
    Write-Output ('Evidence: ' + (Join-Path $stage 'visible-result.json'))
    Write-Output ('Screenshot: ' + $screenshot)
} catch {
    $finding = $_.Exception.GetBaseException().ToString()
    if ($stage -and [IO.Directory]::Exists($stage)) {
        [IO.File]::WriteAllText((Join-Path $stage 'visible-failure.txt'), $finding, $utf8)
    }
    throw
} finally {
    if ($null -ne $window -and !$window.IsDisposed) { $window.Dispose() }
    $clock.Stop()
}
