#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$CodeResultPath)
. "$PSScriptRoot/Review.Common.ps1"
$checks = 0
function Check([bool]$Condition,[string]$Message) { if (!$Condition) { throw $Message }; $script:checks++ }
$review = Get-RC26ReviewedCode $CodeResultPath
$baseline = Get-RC26Baseline
$next = Get-RC26VersionCatalog $baseline.catalog
foreach ($profile in @('full','potato')) {
    $restored = ConvertTo-RC26Json $next.$profile | ConvertFrom-Json
    Check ($restored.packVersion -ceq 'kesas-rc26' -and $restored.requiredClientVersion -ceq 'kesas-rc26') 'Profile revision routing missing.'
    $changed = @()
    foreach ($old in $baseline.catalog.$profile.managedPackages) {
        $package = @($restored.managedPackages | Where-Object id -CEQ $old.id)
        Check ($package.Count -eq 1) 'Package disappeared or duplicated.'
        if ($package[0].version -cne $old.version) {
            Check ($old.id -cin @(Get-RC26RepairIds) -and $package[0].version -ceq ($old.version + '-repair26')) 'Unexpected package version diff.'
            $changed += $old.id; $package[0].version = $old.version
        }
        Check ((ConvertTo-RC26Json $package[0]) -ceq (ConvertTo-RC26Json $old)) 'Archive URL/hash/size or package policy changed.'
    }
    Check ((ConvertTo-RC26Json @($changed | Sort-Object)) -ceq (ConvertTo-RC26Json @(Get-RC26RepairIds | Sort-Object))) 'Exactly four repairs per profile required.'
    $restored.packVersion = 'kesas-rc25'; $restored.requiredClientVersion = 'kesas-rc25'
    Check ((ConvertTo-RC26Json $restored) -ceq (ConvertTo-RC26Json $baseline.catalog.$profile)) 'Profile policy drift.'
}
Check (@($next.full.managedPackages).Count -eq 211 -and @($next.potato.managedPackages).Count -eq 177) 'Profile counts changed.'
Check ((ConvertTo-RC26Json $next.gitHub) -ceq (ConvertTo-RC26Json $baseline.catalog.gitHub)) 'GitHub settings changed.'
Check ($review.audit.preservedMethods -eq 6326 -and $review.audit.preservedCoreMethods -eq 689) 'Unexpected preservation counts.'
$attempt = New-RC26Attempt 'Test'
$fixture = $attempt.pending
$tool = "$($review.directory)/Tool/VersionPatcher.dll"
$dotnet = "$RC26Workspace/tools/dotnet-sdk8/dotnet.exe"
$savedManaged = [Environment]::GetEnvironmentVariable('APF_RC26_UNITY_MANAGED','Process')
try {
    [Environment]::SetEnvironmentVariable('APF_RC26_UNITY_MANAGED',$review.report.unityManaged,'Process')
    foreach ($kind in @('client','core','audit')) {
        $directory = Get-UpdateChildPath $fixture $kind
        Copy-UpdateTree "$($review.directory)/Patched" $directory
        $name = switch ($kind) { client { 'LmpClient.dll' } core { 'ApfCore.dll' } audit { 'audit.json' } }
        $target = Get-UpdateChildPath $directory $name
        $bytes = [IO.File]::ReadAllBytes($target); $bytes[$bytes.Length - 1] = $bytes[$bytes.Length - 1] -bxor 1
        [IO.File]::WriteAllBytes($target,$bytes)
        & $dotnet $tool review $baseline.feedPath $baseline.archive "$RC26Root/secrets/update-signing-public.xml" "$RC26Root/UpdateTool/ApfUpdateTool.exe" `
            "$($review.directory)/Baseline/GameData/LunaMultiplayer/Plugins/ApfCore.dll" "$($review.directory)/Baseline/GameData/LunaMultiplayer/Plugins/LmpClient.dll" $directory "$($review.directory)/Engine/KesasInstall.dll"
        Check ($LASTEXITCODE -ne 0) "Tampered $kind was accepted."
    }
    $gateFailed = $false
    try { & "$RC26Work/Build-Code.ps1" -Mode All } catch { $gateFailed = $_.Exception.Message -ceq 'Main must freeze Engine edits before using -Mode All -EngineFrozen.' }
    Check $gateFailed 'Engine freeze gate did not fail before build.'
    $gateFailed = $false
    try { & "$RC26Work/Build-Release.ps1" -CodeResultPath $CodeResultPath -MediaMode ReuseRC25 -CompatibilityEvidencePath $CodeResultPath -CompatibilityEvidenceSha256 (Get-RC26Hash $CodeResultPath) -Notes 'Gate test only; no release authorized.' -PotatoPolicyPlanPath $CodeResultPath -PotatoPolicyPlanSha256 (Get-RC26Hash $CodeResultPath) }
    catch { $gateFailed = $_.Exception.Message -ceq 'Release preparation requires main approval; use -ReleaseApproved only after the final media/baseline decision.' }
    Check $gateFailed 'Release gate did not fail before candidate creation.'
} finally { [Environment]::SetEnvironmentVariable('APF_RC26_UNITY_MANAGED',$savedManaged,'Process') }
Write-RC26Json "$fixture/TEST-RESULT.json" ([ordered]@{ checks = $checks; passed = $true; codeResultPath = $CodeResultPath; catalogDiff = 'Revision routing plus exactly four package-version bumps in each profile; archive declarations unchanged.'
    tamperedClientRejected = $true; tamperedCoreRejected = $true; tamperedAuditRejected = $true; engineFreezeGate = $true; releaseApprovalGate = $true
    engineBuilt = $false; releaseBuilt = $false; nativeTestsExecuted = $false; liveWrites = $false })
Complete-RC26Attempt $attempt
Write-Output "PASS $checks pipeline checks: $($attempt.complete)/TEST-RESULT.json"
