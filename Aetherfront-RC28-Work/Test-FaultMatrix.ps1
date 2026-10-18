#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PassedSettingsResultPath,
    [string]$ExecutableSha256='',
    [switch]$Run
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$faultWork=$PSScriptRoot
$faultWorkspace=Split-Path $faultWork
function Get-FaultHash([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()}
function Assert-FaultHash([string]$Path,[string]$Expected){
    if($Expected -notmatch '^[0-9a-fA-F]{64}$' -or (Get-FaultHash $Path) -ine $Expected){throw "Frozen input hash mismatch: $Path"}
}
function Assert-FaultQuiet {
    $faultOther=@(Get-Process -Name KSP_x64,KSP,SettingsTests,NativeTests,CasingTests,MonoHost -ErrorAction SilentlyContinue)
    if($faultOther.Count){throw 'Other KSP/test processes are active; do not overlap legacy runs.'}
}
$faultBasePath=[IO.Path]::GetFullPath($PassedSettingsResultPath)
$faultRunnerHash=Get-FaultHash $PSCommandPath
$faultBaseHash=Get-FaultHash $faultBasePath
$faultBase=Get-Content -LiteralPath $faultBasePath -Raw | ConvertFrom-Json
if(!$faultBase.passed -or !$faultBase.compiled -or $faultBase.buildOnly -or $faultBase.preliminary){throw 'A passed, non-preliminary full Settings result is required.'}
$faultExpectedModes=@('new','old27','migration26','migration27','fresh26','fresh27')
$faultExpectedRuntimes=@('CLR','KSP-Mono')
if(@($faultBase.runs).Count -ne 12){throw 'Base result must contain exactly six modes on both runtimes.'}
$faultCoreHashes=[Collections.Generic.List[string]]::new()
foreach($faultRuntime in $faultExpectedRuntimes){foreach($faultMode in $faultExpectedModes){
    $faultRecord=@($faultBase.runs | Where-Object {$_.runtime -ceq $faultRuntime -and $_.mode -ceq $faultMode})
    if($faultRecord.Count -ne 1 -or $faultRecord[0].exitCode -ne 0){throw 'Base result is missing a successful required run.'}
    $faultSummary=Get-Content -LiteralPath $faultRecord[0].summary -Raw | ConvertFrom-Json
    if($faultSummary.runtime -cne $faultRuntime -or $faultSummary.mode -cne $faultMode -or $faultSummary.cases -lt 1 -or $faultSummary.checks -lt 1 -or @($faultSummary.failures).Count){throw 'Base run summary does not prove success.'}
    if($faultSummary.legacyFaultCheckpoint){throw 'Base result must be the regular matrix, not checkpoint runs.'}
    Assert-FaultHash $faultRecord[0].enginePath $faultRecord[0].engineSha256
    if($faultSummary.engineSha256 -ine $faultRecord[0].engineSha256){throw 'Base summary Engine binding mismatch.'}
    $faultCoreHashes.Add($faultSummary.coreSha256)
}}
$faultEngineResultPath=[IO.Path]::GetFullPath($faultBase.engineResultPath)
$faultEngineResultHash=Get-FaultHash $faultEngineResultPath
$faultEngineResult=Get-Content -LiteralPath $faultEngineResultPath -Raw | ConvertFrom-Json
if(!$faultEngineResult.completed -or $faultEngineResult.version -cne 'kesas-rc28' -or $faultEngineResult.engineSha256 -ine $faultBase.engineSha256 -or [IO.Path]::GetFullPath($faultEngineResult.enginePath) -ine [IO.Path]::GetFullPath($faultBase.enginePath)){throw 'Base and frozen Engine result disagree.'}
$faultInputs=[Collections.Generic.List[object]]::new()
foreach($faultSource in @($faultBase.sources)+@($faultEngineResult.sources)){
    Assert-FaultHash $faultSource.path $faultSource.sha256
    $faultInputs.Add([ordered]@{path=$faultSource.path;sha256=$faultSource.sha256})
}
$faultExecutable=[IO.Path]::GetFullPath($faultBase.executable)
$faultRecordedExecutable=$faultBase.PSObject.Properties['executableSha256']
if($faultRecordedExecutable){
    if($ExecutableSha256 -and $ExecutableSha256 -ine $faultRecordedExecutable.Value){throw 'Explicit executable SHA disagrees with base report.'}
    $ExecutableSha256=[string]$faultRecordedExecutable.Value
}
if(!$ExecutableSha256){throw 'Base report has no executable SHA; pass -ExecutableSha256 from the trusted completed build.'}
Assert-FaultHash $faultExecutable $ExecutableSha256
$faultOld=@{}
foreach($faultMode in @('migration26','migration27')){
    $faultHelpers=@($faultBase.runs | Where-Object mode -CEQ $faultMode)
    if(@($faultHelpers.engineSha256 | Select-Object -Unique).Count -ne 1){throw 'Historical helper hashes disagree between runtimes.'}
    $faultOld[$faultMode]=$faultHelpers[0]
}
$faultCore=Join-Path $faultBase.baselinePath 'GameData/LunaMultiplayer/Plugins/ApfCore.dll'
if(@($faultCoreHashes | Select-Object -Unique).Count -ne 1){throw 'Base runs did not all use the same normal Core.'}
Assert-FaultHash $faultCore $faultCoreHashes[0]
$faultMonoHost=Join-Path $faultWorkspace 'Aetherfront-RC22-Work/MonoHost/bin/Release/net472/MonoHost.exe'
$faultInputs.Add([ordered]@{path=$faultExecutable;sha256=$ExecutableSha256.ToLowerInvariant()})
$faultInputs.Add([ordered]@{path=$faultBase.enginePath;sha256=$faultBase.engineSha256})
$faultInputs.Add([ordered]@{path=$faultCore;sha256=$faultCoreHashes[0]})
$faultInputs.Add([ordered]@{path=$faultMonoHost;sha256=(Get-FaultHash $faultMonoHost)})
$faultInputs.Add([ordered]@{path=$faultBasePath;sha256=$faultBaseHash})
$faultInputs.Add([ordered]@{path=$faultEngineResultPath;sha256=$faultEngineResultHash})
$faultInputs.Add([ordered]@{path=$PSCommandPath;sha256=$faultRunnerHash})
foreach($faultHelper in $faultOld.Values){$faultInputs.Add([ordered]@{path=$faultHelper.enginePath;sha256=$faultHelper.engineSha256})}
if(!$Run){[pscustomobject]@{validated=$true;launched=$false;plannedScenarios=12;executableSha256=$ExecutableSha256;engineSha256=$faultBase.engineSha256}|ConvertTo-Json -Compress;return}
Assert-FaultQuiet
$faultRoot=Join-Path $faultWork ('P'+[Guid]::NewGuid().ToString('N').Substring(0,8))
if((Split-Path $faultRoot) -ine $faultWork -or (Split-Path $faultRoot -Leaf) -cnotmatch '^P[0-9a-f]{8}$'){throw 'Unsafe private output root.'}
[void][IO.Directory]::CreateDirectory($faultRoot)
$faultRuns=[Collections.Generic.List[object]]::new()
$faultReportPath=Join-Path $faultRoot 'FAULT-MATRIX-RESULT.json'
$faultFatal=$null
$faultPreviousArguments=[Environment]::GetEnvironmentVariable('APF_SETTINGS_ARGUMENTS','Process')
$faultCheckpoints=@('plan-saved','file-written:0','file-recorded:0')
try{
    for($faultC=0;$faultC -lt 3;$faultC++){for($faultR=0;$faultR -lt 2;$faultR++){for($faultM=0;$faultM -lt 2;$faultM++){
        Assert-FaultQuiet
        foreach($faultInput in $faultInputs){Assert-FaultHash $faultInput.path $faultInput.sha256}
        $faultRuntime=$faultExpectedRuntimes[$faultR];$faultMode=@('migration26','migration27')[$faultM];$faultCheckpoint=$faultCheckpoints[$faultC]
        $faultOutput=Join-Path $faultRoot "r${faultR}m${faultM}c${faultC}"
        $faultArguments=@('--mode',$faultMode,'--engine',$faultOld[$faultMode].enginePath,'--new-engine',$faultBase.enginePath,'--baseline',$faultBase.baselinePath,'--output',$faultOutput,'--expected-runtime',$faultRuntime,'--legacy-fault-checkpoint',$faultCheckpoint)
        if(@($faultArguments | Where-Object {$_ -match '[\r\n]'}).Count){throw 'Test arguments must not contain line breaks.'}
        $faultSummaryPath=Join-Path $faultOutput 'summary.json'
        $faultRecord=[ordered]@{checkpoint=$faultCheckpoint;runtime=$faultRuntime;mode=$faultMode;exitCode=$null;checks=0;failures=@();summaryPath=$faultSummaryPath;summarySha256=$null;logPath="$faultOutput.log";passed=$false}
        $faultRuns.Add($faultRecord)
        [Environment]::SetEnvironmentVariable('APF_SETTINGS_ARGUMENTS',$null,'Process')
        if($faultRuntime -eq 'CLR'){
            & $faultExecutable @faultArguments *> "$faultOutput.log";$faultExit=$LASTEXITCODE
        }else{
            [Environment]::SetEnvironmentVariable('APF_SETTINGS_ARGUMENTS',($faultArguments -join "`n"),'Process')
            Push-Location $faultWorkspace
            try{& $faultMonoHost $faultExecutable *> "$faultOutput.log";$faultExit=$LASTEXITCODE}finally{Pop-Location;[Environment]::SetEnvironmentVariable('APF_SETTINGS_ARGUMENTS',$null,'Process')}
        }
        $faultRecord.exitCode=$faultExit
        $faultSummary=Get-Content -LiteralPath $faultSummaryPath -Raw | ConvertFrom-Json
        $faultRecord.checks=$faultSummary.checks;$faultRecord.failures=@($faultSummary.failures);$faultRecord.summarySha256=Get-FaultHash $faultSummaryPath
        if($faultExit -ne 0 -or $faultSummary.cases -ne 1 -or @($faultSummary.failures).Count -or $faultSummary.legacyFaultCheckpoint -cne $faultCheckpoint -or $faultSummary.runtime -cne $faultRuntime -or $faultSummary.mode -cne $faultMode -or $faultSummary.engineSha256 -ine $faultOld[$faultMode].engineSha256){throw 'Fault scenario failed; remaining scenarios were not launched.'}
        $faultFixtures=@(Get-ChildItem -LiteralPath $faultOutput -Directory -Filter 'F*')
        if($faultFixtures.Count -ne 1){throw 'Expected exactly one real migration fixture.'}
        $faultProofPath=Join-Path $faultFixtures[0].FullName 'startup-fault-proof.json'
        $faultProof=Get-Content -LiteralPath $faultProofPath -Raw | ConvertFrom-Json
        $faultLaunch=Join-Path $faultFixtures[0].FullName 'startup-launch.txt'
        if(!$faultProof.reached -or !$faultProof.observed -or !$faultProof.sameStartup -or $faultProof.checkpoint -cne $faultCheckpoint -or $faultProof.recoveryCalls -ne 2 -or @(Get-Content -LiteralPath $faultLaunch).Count -ne 1){throw 'Actual recovery interruption/resume or single-startup proof is missing.'}
        $faultRecord['faultProofPath']=$faultProofPath;$faultRecord['faultProofSha256']=Get-FaultHash $faultProofPath;$faultRecord['recoveryStartupRuntime']=$faultProof.runtime;$faultRecord.passed=$true
        "Fault $faultCheckpoint $faultRuntime $faultMode passed checks=$($faultSummary.checks)"
    }}}
    foreach($faultInput in $faultInputs){Assert-FaultHash $faultInput.path $faultInput.sha256}
}catch{$faultFatal=$_.Exception.Message}
finally{
    [Environment]::SetEnvironmentVariable('APF_SETTINGS_ARGUMENTS',$faultPreviousArguments,'Process')
    $faultReport=[ordered]@{passed=(!$faultFatal -and $faultRuns.Count -eq 12);plannedScenarios=12;launchedScenarios=$faultRuns.Count;completedScenarios=@($faultRuns|Where-Object summarySha256).Count;fatalError=$faultFatal;baseSettingsResultPath=$faultBasePath;baseSettingsResultSha256=$faultBaseHash;executablePath=$faultExecutable;executableSha256=$ExecutableSha256.ToLowerInvariant();engineResultPath=$faultEngineResultPath;engineResultSha256=$faultEngineResultHash;engineSha256=$faultBase.engineSha256;inputs=$faultInputs;runnerSha256=$faultRunnerHash;runs=$faultRuns;unityGameplayTested=$false;fixturesRetained=$true}
    [IO.File]::WriteAllText($faultReportPath,($faultReport|ConvertTo-Json -Depth 12))
    "FAULT-MATRIX-RESULT: $faultReportPath"
}
if($faultFatal){throw $faultFatal}
