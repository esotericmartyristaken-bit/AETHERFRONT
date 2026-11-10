#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Archive,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$ArchiveSha256,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$SourceCommit,
    [Parameter(Mandatory)][string]$ReportPath,
    [string]$DeploymentRoot='',
    [string]$BotConfig='',
    [string]$Notes='Verified combined Aetherfront update',
    [string]$ScienceCompensationPlan='',
    [string]$ScienceCompensationSha256=''
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Initialize-LocalPublishEnvironment.ps1')
$git=Initialize-LocalPublishEnvironment
$sourceRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$control=Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'Aetherfront-Control'
$hostSettings=Get-Content -LiteralPath (Join-Path $control 'host.json') -Raw|ConvertFrom-Json
if(-not $DeploymentRoot){$DeploymentRoot=$hostSettings.nativeRoot}
if(-not $BotConfig){$BotConfig=Join-Path $hostSettings.botRoot 'config.json'}
$deployment=[IO.Path]::GetFullPath($DeploymentRoot)
$archivePath=[IO.Path]::GetFullPath($Archive)
$reportAuthorized=$false
$report=[ordered]@{at=[DateTime]::UtcNow.ToString('o');stage='preflight';healthy=$false;sourceCommit=$SourceCommit;archiveSha256=$ArchiveSha256;remoteHostDeployment=$false;error=$null}
$operatorLock=$null;$publicationProbe=$null
$priorTask=$env:APF_SERVER_TASK;$priorControl=$env:APF_CONTROL_ROOT
function Report-Local([string]$Stage){
    if(-not $reportAuthorized){return}
    $report.stage=$Stage;$report.updatedAt=[DateTime]::UtcNow.ToString('o')
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($ReportPath),($report|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
}
function New-LocalPrivateDirectory([string]$Path){
    if(Test-Path -LiteralPath $Path){
        $entry=Get-Item -LiteralPath $Path -Force
        if($entry.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Private deployment path is a reparse point'}
        $acl=Get-Acl -LiteralPath $Path
        if(-not $acl.AreAccessRulesProtected -or $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin @('S-1-5-18','S-1-5-32-544')){throw 'Existing local deployment directory is not protected and trusted-owned'}
        foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){
            if($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -notin @('S-1-5-18','S-1-5-32-544')){throw 'Existing local deployment directory is not private'}
        }
        return
    }
    $acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false)
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    foreach($sid in @('S-1-5-18','S-1-5-32-544')){
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
    }
    [IO.FileSystemAclExtensions]::Create([IO.DirectoryInfo]::new($Path),$acl)
}
try{
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    if(-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Run the reviewed local deployment with normal Windows UAC approval'}
    if($deployment -ine [IO.Path]::GetFullPath($hostSettings.nativeRoot)){throw 'Unexpected live deployment root'}
    $reportFull=[IO.Path]::GetFullPath($ReportPath)
    $reportsRoot=[IO.Path]::GetFullPath((Join-Path $sourceRoot '../UpdateBuilds')).TrimEnd('\')+'\'
    if(-not $reportFull.StartsWith($reportsRoot,[StringComparison]::OrdinalIgnoreCase) -or [IO.File]::Exists($reportFull)){throw 'Use a new report file inside workspace UpdateBuilds'}
    $reportAuthorized=$true
    if((Get-FileHash -LiteralPath $archivePath).Hash.ToLowerInvariant() -cne $ArchiveSha256){throw 'Release archive hash differs'}
    if((& $git -C $sourceRoot rev-parse HEAD).Trim() -cne $SourceCommit){throw 'Reviewed source commit differs'}
    & $git -C $sourceRoot diff --quiet HEAD -- KESAS-VPS-Server APFDiscordBot/src APFDiscordBot/scripts ControlPlane Aetherfront-RC44-Work/Deployment
    if($LASTEXITCODE -ne 0){throw 'Reviewed deployment code is dirty'}
    $configuration=Get-Content -LiteralPath $BotConfig -Raw|ConvertFrom-Json
    if($configuration.updates.githubDeployment.enabled -ne $false){throw 'Disable Git-triggered host deployment before local publication'}
    if($configuration.updates.hostDeploymentMode -cne 'local-only'){throw 'Require local-only host deployment mode'}
    if([IO.Path]::GetFullPath($configuration.lmp.deploymentRoot) -ine $deployment){throw 'Bot and local deployment roots differ'}
    $node=$hostSettings.node
    $privateRoot='C:\ProgramData\Aetherfront-Local'
    New-LocalPrivateDirectory $privateRoot
    $operatorLock=[IO.FileStream]::new((Join-Path $privateRoot '.operator.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $publicationProbe=[IO.FileStream]::new((Join-Path $deployment '.publish.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $updateState=Get-Content -LiteralPath (Join-Path $hostSettings.botRoot $configuration.updates.stateFile) -Raw|ConvertFrom-Json
    if($updateState.schemaVersion -ne 1 -or $updateState.status -notin @('idle','failed')){throw 'Another bot update operation requires review; no service stopped'}
    if(Test-Path -LiteralPath "$deployment/.update-transaction.json"){throw 'An existing native activation/completion journal requires review first'}
    foreach($file in Get-ChildItem -LiteralPath "$control/native-cache/results" -Filter '*.json' -File){
        $value=Get-Content -LiteralPath $file.FullName -Raw|ConvertFrom-Json
        if($value.phase -notin @('healthy','blocked')){throw 'A native request remains active or ambiguous'}
    }
    $bundle=Join-Path $privateRoot ("releases/$SourceCommit-$($ArchiveSha256.Substring(0,16))")
    if(Test-Path -LiteralPath $bundle){throw 'Use a new reviewed local deployment bundle; do not replace a prior attempt'}
    New-LocalPrivateDirectory $bundle
    $selected=[Collections.Generic.List[string]]::new()
    foreach($selection in @(
        @{path='KESAS-VPS-Server';filter='*.ps1'},
        @{path='Aetherfront-RC44-Work/Deployment';filter='*.ps1'},
        @{path='Aetherfront-RC44-Work/Deployment';filter='*.mjs'},
        @{path='Aetherfront-RC44-Work/Deployment';filter='*.cs'},
        @{path='APFDiscordBot/src';filter='*.js'},
        @{path='APFDiscordBot/scripts';filter='*.ps1'},
        @{path='ControlPlane';filter='*.mjs'}
    )){
        foreach($file in Get-ChildItem -LiteralPath (Join-Path $sourceRoot $selection.path) -Filter $selection.filter -File){$selected.Add([IO.Path]::GetRelativePath($sourceRoot,$file.FullName))}
    }
    $selected.Add('APFDiscordBot/package.json')
    $selected.Add('APFDiscordBot/package-lock.json')
    $files=@(foreach($relative in $selected){
        $source=Join-Path $sourceRoot $relative;$destination=Join-Path $bundle $relative
        if((Get-Item -LiteralPath $source -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Reviewed source contains a reparse point'}
        $committed=@(& $git -C $sourceRoot rev-parse --verify "${SourceCommit}:$($relative.Replace('\','/'))")
        if($LASTEXITCODE -ne 0 -or $committed.Count -ne 1){throw 'Frozen source is not tracked in reviewed commit'}
        $working=@(& $git -C $sourceRoot hash-object --path=$relative $source)
        if($LASTEXITCODE -ne 0 -or $working.Count -ne 1 -or $working[0] -cne $committed[0]){throw 'Frozen source differs from reviewed commit blob'}
        $sha=(Get-FileHash -LiteralPath $source).Hash.ToLowerInvariant()
        $null=[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination));Copy-Item -LiteralPath $source -Destination $destination
        if((Get-FileHash -LiteralPath $destination).Hash.ToLowerInvariant() -cne $sha){throw 'Frozen local deployment source differs'}
        @{path=$relative;sha256=$sha}
    })
    [IO.File]::WriteAllText((Join-Path $bundle 'SOURCE.json'),(@{commit=$SourceCommit;files=$files}|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
    $frozenArchive=Join-Path $bundle 'Release.zip';Copy-Item -LiteralPath $archivePath -Destination $frozenArchive
    if((Get-FileHash -LiteralPath $frozenArchive).Hash.ToLowerInvariant() -cne $ArchiveSha256){throw 'Frozen archive differs from reviewed release'}
    $frozenCompensation=''
    if($ScienceCompensationPlan){
        if($ScienceCompensationSha256 -cnotmatch '^[a-f0-9]{64}$' -or (Get-FileHash -LiteralPath $ScienceCompensationPlan).Hash.ToLowerInvariant() -cne $ScienceCompensationSha256){throw 'Reviewed compensation plan differs'}
        $frozenCompensation=Join-Path $bundle 'ScienceCompensation.json'
        Copy-Item -LiteralPath $ScienceCompensationPlan -Destination $frozenCompensation
        if((Get-FileHash -LiteralPath $frozenCompensation).Hash.ToLowerInvariant() -cne $ScienceCompensationSha256){throw 'Frozen compensation plan differs'}
    }elseif($ScienceCompensationSha256){throw 'Compensation hash requires a plan'}
    $report.bundle=$bundle;Report-Local 'verifying-server-task'
    # Validate the unchanged protected startup route before bot/media work.
    . (Join-Path $bundle 'Aetherfront-RC44-Work/Deployment/Start-PreactivationRecovery.ps1')
    Initialize-PreactivationRecovery
    Assert-RecoveryTaskBinding @{ControlRoot=$control;NativeRoot=$deployment}
    $report.gameTaskBindingVerified=$true
    # Exercise real Windows firewall syntax before bot changes, long media work,
    # or native startup. Only a NEW private, never-executed copy is targeted.
    Report-Local 'verifying-candidate-firewall'
    . (Join-Path $bundle 'KESAS-VPS-Server/Update.Common.ps1')
    . (Join-Path $bundle 'Aetherfront-RC44-Work/Deployment/Test-PreparedCandidate.ps1')
    $probeRoot=New-PCPrivateDirectory (Join-Path $bundle 'FirewallProbe')
    $null=Copy-PCFile (Join-Path $deployment 'Server/Server.exe') (Join-Path $probeRoot 'Probe.exe')
    $probeContext=@{Candidate=$probeRoot;GamePort=28865;PackPort=28866;Receipt=@{};Firewall=[Collections.Generic.List[string]]::new();Owned=[Collections.Generic.List[object]]::new()}
    try{Enable-PCIsolation $probeContext}finally{Disable-PCIsolation $probeContext}
    $report.firewallApiVerified=$true;Report-Local 'local-mode-reload'
    # The live bridge atomically replaces this file. Use the same bounded,
    # freshness-checked reader as admission, not PowerShell's restrictive share.
    . (Join-Path $bundle 'Aetherfront-RC44-Work/Deployment/LocalAdmission.ps1')
    $initialBridge=Invoke-LocalDeploymentBridge $node (Join-Path $bundle 'Aetherfront-RC44-Work/Deployment') $BotConfig 'status'
    $report.previousGamePid=$initialBridge.serverPid
    . (Join-Path $bundle 'Aetherfront-RC44-Work/Deployment/Install-LocalBot.ps1')
    $localBot=Install-LocalBot $bundle $hostSettings $BotConfig
    $report.localBot=$localBot
    $publicationProbe.Dispose();$publicationProbe=$null
    if(-not $env:APF_DISCORD_TOKEN){$env:APF_DISCORD_TOKEN=[Environment]::GetEnvironmentVariable('APF_DISCORD_TOKEN','User')}
    if(-not $env:APF_DISCORD_TOKEN){throw 'Local deployment announcements need the configured bot credential'}
    $env:APF_SERVER_TASK='KESAS Server';$env:APF_CONTROL_ROOT=$control
    $publisher=Join-Path $bundle 'KESAS-VPS-Server/Publish-Release.ps1'
    Report-Local 'preparing-and-verifying-candidate'
    $evidence=Join-Path $bundle 'Evidence';New-LocalPrivateDirectory $evidence
    & $publisher -ReleaseZip $frozenArchive -Notes $Notes -DeploymentRoot $deployment -LocalDeployment -LocalBotConfig $BotConfig -LocalNode $node -LocalEvidenceRoot $evidence -ScienceCompensationPlan $frozenCompensation -ScienceCompensationSha256 $ScienceCompensationSha256
    if(-not $?){throw 'Local publisher did not complete'}
    $pack=Get-Content -LiteralPath "$deployment/Server/Config/APFPack.json" -Raw|ConvertFrom-Json
    $feed=Get-Content -LiteralPath "$deployment/Updates/APFClientUpdate.json" -Raw|ConvertFrom-Json
    if($pack.packVersion -cne $feed.version -or $pack.requiredClientVersion -cne $feed.version){throw 'Completed pack/feed versions differ'}
    & "$deployment/CertificateTool/ApfCertificateTool.exe" --probe-server 'https://127.0.0.1:18866/health' '34b4bf414b4923fdf8150d0c56a541d393a45bf76dd50ae10e93afc5d1358d09'
    if($LASTEXITCODE -ne 0){throw 'Completed pinned HTTPS health failed'}
    $completion=Join-Path $bundle 'completion.json'
    [IO.File]::WriteAllText($completion,(@{version=$feed.version;notes=$Notes}|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    & $node (Join-Path $bundle 'Aetherfront-RC44-Work/Deployment/LocalBridge.mjs') complete $BotConfig $completion
    if($LASTEXITCODE -ne 0){throw 'Active release passed; local completion announcement/state still needs review'}
    $report.version=$feed.version;$report.healthy=$true;Report-Local 'healthy'
}catch{
    $report.error=$_.Exception.Message;Report-Local 'review-required';throw
}finally{
    if($publicationProbe){$publicationProbe.Dispose()}
    if($operatorLock){$operatorLock.Dispose()}
    $env:APF_SERVER_TASK=$priorTask;$env:APF_CONTROL_ROOT=$priorControl
}
