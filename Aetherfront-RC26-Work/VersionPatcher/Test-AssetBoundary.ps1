#requires -Version 7.0
[CmdletBinding()]
param()
. "$PSScriptRoot/Review.Common.ps1"
$attempt = New-RC26Attempt 'Test'
$fixture = $attempt.pending
$checks = 0
function Check([bool]$Value,[string]$Message) { if (!$Value) { throw $Message }; $script:checks++ }
function New-FixtureZip([string]$Name,$Items,[string]$ChangedMetadata = '') {
    $path = Get-UpdateChildPath $fixture $Name
    $zip = [IO.Compression.ZipFile]::Open($path,[IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($item in $Items.GetEnumerator()) {
            $entry = $zip.CreateEntry($item.Key)
            $entry.LastWriteTime = [DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero)
            if ($item.Key -ceq $ChangedMetadata) { $entry.LastWriteTime = $entry.LastWriteTime.AddDays(1) }
            $entry.ExternalAttributes = 0
            $stream = $entry.Open()
            try { $bytes = $RC26Utf8.GetBytes($item.Value); $stream.Write($bytes) } finally { $stream.Dispose() }
        }
    } finally { $zip.Dispose() }
    return $path
}
function Reject([scriptblock]$Action,[string]$Name) {
    $failed = $false
    try { & $Action } catch { $failed = $true }
    Check $failed "Boundary accepted $Name"
}
$old = [ordered]@{ 'ModularLaunchPads/Textures/source.dds' = 'texture-bytes'; 'ModularLaunchPads/Plugins/original.dll' = 'original-dll'; 'ModularLaunchPads/Parts/original.cfg' = 'original-part' }
$valid = [ordered]@{}; foreach ($item in $old.GetEnumerator()) { $valid[$item.Key] = $item.Value }
$valid['ModularLaunchPads/Textures/alias1.dds'] = 'texture-bytes'; $valid['ModularLaunchPads/Textures/alias2.dds'] = 'texture-bytes'
$aliases = @([pscustomobject]@{ path = 'ModularLaunchPads/Textures/alias1.dds'; sourcePath = 'ModularLaunchPads/Textures/source.dds' },
    [pscustomobject]@{ path = 'ModularLaunchPads/Textures/alias2.dds'; sourcePath = 'ModularLaunchPads/Textures/source.dds' })
$oldZip = New-FixtureZip 'old.zip' $old
$validZip = New-FixtureZip 'valid.zip' $valid
Assert-RC26AssetZipDelta $oldZip $validZip 'kesas-modularlaunchpads-8feabdb7' $aliases '' ''
Check $true 'Valid aliases rejected.'
foreach ($case in @('dll-change','part-change','alias-change','metadata-change','missing-original','extra-file')) {
    $items = [ordered]@{}; foreach ($item in $valid.GetEnumerator()) { $items[$item.Key] = $item.Value }
    $metadata = ''
    switch ($case) {
        dll-change { $items['ModularLaunchPads/Plugins/original.dll'] = 'tampered' }
        part-change { $items['ModularLaunchPads/Parts/original.cfg'] = 'tampered' }
        alias-change { $items['ModularLaunchPads/Textures/alias1.dds'] = 'tampered' }
        metadata-change { $metadata = 'ModularLaunchPads/Textures/source.dds' }
        missing-original { $items.Remove('ModularLaunchPads/Parts/original.cfg') }
        extra-file { $items['ModularLaunchPads/Plugins/new.dll'] = 'new-dll' }
    }
    $zip = New-FixtureZip "$case.zip" $items $metadata
    Reject { Assert-RC26AssetZipDelta $oldZip $zip 'kesas-modularlaunchpads-8feabdb7' $aliases '' '' } $case
}
Reject { Assert-RC26AssetZipDelta $oldZip $validZip 'unreviewed-package' $aliases '' '' } 'unreviewed package'
$badAliases = @([pscustomobject]@{ path = $aliases[0].path; sourcePath = 'ModularLaunchPads/Plugins/original.dll' },$aliases[1])
Reject { Assert-RC26AssetZipDelta $oldZip $validZip 'kesas-modularlaunchpads-8feabdb7' $badAliases '' '' } 'non-texture alias source'
$unsafe = New-FixtureZip 'unsafe.zip' ([ordered]@{ '../escape.dll' = 'unsafe' })
Reject { $null = Get-RC26ZipInventory $unsafe } 'ZIP traversal'
$commonOld = New-FixtureZip 'common-old.zip' ([ordered]@{ 'ZZZ_KESASModFixes/OptionalVisualModules.cfg' = 'unchanged optional visual policy' })
$commonNew = New-FixtureZip 'common-new.zip' ([ordered]@{ 'ZZZ_KESASModFixes/OptionalVisualModules.cfg' = 'unchanged optional visual policy'; 'ZZZ_KESASModFixes/StaticTextureBindings.cfg' = 'reviewed bindings' })
$commonInventory = Get-RC26ZipInventory $commonNew
Assert-RC26AssetZipDelta $commonOld $commonNew 'kesas-rc25-modfixes' @() 'ZZZ_KESASModFixes/StaticTextureBindings.cfg' $commonInventory['ZZZ_KESASModFixes/StaticTextureBindings.cfg'].sha256
Check $true 'Valid common config delta rejected.'
Reject { Assert-RC26AssetZipDelta $commonOld $commonNew 'kesas-rc25-modfixes' @() 'ZZZ_KESASModFixes/another.cfg' $commonInventory['ZZZ_KESASModFixes/StaticTextureBindings.cfg'].sha256 } 'unreviewed common config'
Write-RC26Json "$fixture/TEST-RESULT.json" ([ordered]@{ passed = $true; checks = $checks; assetBoundary = $true; fixturesOnly = $true
    engineBuilt = $false; runtimeCopied = $false; releaseBuilt = $false; liveWrites = $false })
Complete-RC26Attempt $attempt
Write-Output "PASS $checks asset-boundary checks: $($attempt.complete)/TEST-RESULT.json"
