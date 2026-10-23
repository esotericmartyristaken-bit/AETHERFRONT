#requires -Version 7.4
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$SourceCommit,
    [Parameter(Mandatory)][ValidateRange(1,2147483647)][int]$InstallerPid,
    [Parameter(Mandatory)][string]$InstallerStartedAt
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if(-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Normal Windows UAC approval required'}
$repository=Split-Path -Parent $PSScriptRoot
$root='C:\ProgramData\Aetherfront-Control'
$configuration=Get-Content -LiteralPath "$root/host.json" -Raw|ConvertFrom-Json
if([IO.Path]::GetFullPath($configuration.root) -ine $root){throw 'Unexpected protected root'}
. "$root/bootstrap/native/Update.Common.ps1"
$selected=@('anchor.mjs','engine.mjs','contract.mjs','github.mjs','public-status.mjs','control-evidence.mjs','signed-readback.mjs','observation-publisher.mjs','supervision.mjs','Expand-ControlBundle.ps1','Manage-ControlWorker.ps1','Sign-HostObservation.ps1')
$output=Join-Path (Split-Path -Parent $repository) 'TestResults/control-repair-resume.json'
$result=[ordered]@{schemaVersion=1;sourceCommit=$SourceCommit;installerPid=$InstallerPid;stage='preflight';completed=$false;gameRestarted=$false;packRestarted=$false;reason=$null}
$lease=$null
function Report([string]$Stage){$result.stage=$Stage;$result.observedAt=[DateTime]::UtcNow.ToString('o');[IO.File]::WriteAllText($output,($result|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))}
function Native-Idle {
    $state=Get-Content -LiteralPath "$($configuration.botRoot)/state/kesas-update-state.json" -Raw|ConvertFrom-Json
    if($state.schemaVersion -ne 1 -or $state.status -notin @('idle','failed') -or (Test-Path -LiteralPath "$($configuration.nativeRoot)/.update-transaction.json")){throw 'Native application operation must remain untouched'}
    foreach($file in Get-ChildItem -LiteralPath "$root/native-cache/results" -Filter '*.json' -File){$value=Get-Content -LiteralPath $file.FullName -Raw|ConvertFrom-Json;if($value.phase -notin @('healthy','blocked')){throw 'Active or ambiguous native helper must remain untouched'}}
}
function Game-Identity {
    [ordered]@{game=@(Get-NetUDPEndpoint -LocalPort 18865).OwningProcess;pack=@(Get-NetTCPConnection -LocalPort 18866 -State Listen).OwningProcess}
}
function Test-ResumeIdentity($Process,[string]$Executable,[string]$Script,[DateTime]$Started){
    return $null -ne $Process -and $Process.ExecutablePath -ieq $Executable -and $Process.CreationDate.ToUniversalTime() -eq $Started -and
        $Process.CommandLine -match ('(?i)-File\s+"?'+[regex]::Escape($Script)+'"?(?:\s|$)') -and
        $Process.CommandLine -match '(?i)-SourceCommit\s+660486ea9c180c1716fd75b38edd7f8c24174293(?:\s|$)'
}
try{
    Report 'preflight'
    if((& git -C $repository rev-parse HEAD).Trim() -cne $SourceCommit){throw 'Reviewed source commit changed'}
    & git -C $repository diff --quiet HEAD -- ControlPlane SOURCE-MANIFEST.json
    if($LASTEXITCODE -ne 0){throw 'Reviewed source is dirty'}
    $manifestText=(& git -C $repository show "${SourceCommit}:SOURCE-MANIFEST.json"|Out-String)
    if($LASTEXITCODE -ne 0){throw 'Reviewed source manifest unavailable'}
    $manifest=$manifestText|ConvertFrom-Json
    # Read once, verify committed bytes, then extract only rollback helpers.
    $installerPath=Join-Path $PSScriptRoot 'Repair-HostControl.ps1'
    $installerBytes=[IO.File]::ReadAllBytes($installerPath)
    $entry=@($manifest.files|Where-Object path -CEQ 'ControlPlane/Repair-HostControl.ps1')
    if($entry.Count -ne 1 -or [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($installerBytes)).ToLowerInvariant() -cne $entry[0].sha256){throw 'Reviewed installer bytes changed'}
    $tokens=$null;$errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseInput([Text.Encoding]::UTF8.GetString($installerBytes),[ref]$tokens,[ref]$errors)
    if($errors.Count){throw 'Reviewed installer does not parse'}
    foreach($name in @('Assert-RepairHash','Restore-RepairBootstrap')){
        $definition=@($ast.FindAll({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name},$true))
        if($definition.Count -ne 1){throw 'Reviewed rollback helper is ambiguous'}
        Invoke-Expression $definition[0].Extent.Text
    }
    $old=Get-CimInstance Win32_Process -Filter "ProcessId=$InstallerPid"
    $started=[DateTimeOffset]::Parse($InstallerStartedAt).UtcDateTime
    if(-not (Test-ResumeIdentity $old $configuration.powershell $installerPath $started)){throw 'Stalled installer identity differs'}
    $candidates=@(Get-ChildItem -LiteralPath "$root/native-cache" -Directory|Where-Object {$_.Name -match '^control-repair-\d{8}-\d{6}-[a-f0-9]{32}$' -and $_.CreationTimeUtc -ge $started.AddSeconds(-2)})
    if($candidates.Count -ne 1){throw 'Protected prior maintenance snapshot is ambiguous'}
    $history=Assert-UpdatePath $candidates[0].FullName
    $rows=@(Get-Content -LiteralPath (Join-Path $history 'bootstrap-before.json') -Raw|ConvertFrom-Json)
    if($rows.Count -ne $selected.Count -or @($rows.name|Sort-Object -Unique).Count -ne $selected.Count -or @($rows|Where-Object {$_.name -cnotin $selected}).Count){throw 'Prior bootstrap inventory differs'}
    $backup=Join-Path $history 'prior-bootstrap'
    foreach($row in $rows){if($row.existed){Assert-RepairHash (Join-Path $backup $row.name) $row.sha256}}
    $prior=@(Get-Content -LiteralPath (Join-Path $history 'control-task-state.json') -Raw|ConvertFrom-Json)
    if($prior.Count -ne 2 -or @($prior|Where-Object {$_.name -cnotin @('APF Discord Bot','Aetherfront Native Publisher') -or $_.enabled -ne $true -or $_.running -ne $true}).Count -or @($prior.name|Sort-Object -Unique).Count -ne 2){throw 'Prior control task state differs'}
    $scheduler=New-Object -ComObject 'Schedule.Service';$scheduler.Connect()
    $tasks=@(foreach($name in @('APF Discord Bot','Aetherfront Native Publisher')){$scheduler.GetFolder('\').GetTask($name)})
    if(@($tasks|Where-Object {$_.Enabled -or $_.State -ne 1}).Count){throw 'Prior maintenance is not suspended'}
    Native-Idle
    $before=Game-Identity
    if($InstallerPid -in (@($before.game)+@($before.pack))){throw 'Invalid installer stop target'}
    Report 'stopping-stalled-control-installer'
    $current=Get-CimInstance Win32_Process -Filter "ProcessId=$InstallerPid"
    if(-not $current -or $current.CreationDate -ne $old.CreationDate -or $current.CommandLine -cne $old.CommandLine){throw 'Stalled installer identity changed'}
    Stop-Process -Id $InstallerPid -Force -ErrorAction Stop
    $deadline=[DateTime]::UtcNow.AddSeconds(10)
    while(Get-Process -Id $InstallerPid -ErrorAction SilentlyContinue){if([DateTime]::UtcNow -ge $deadline){throw 'Control installer did not stop'};Start-Sleep -Milliseconds 100}
    $lease=[IO.FileStream]::new((Join-Path $configuration.nativeRoot '.publish.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    Native-Idle
    Report 'restoring-prior-control-baseline'
    $processes=@(Get-CimInstance Win32_Process|Where-Object {$_.Name -eq 'node.exe' -and $_.ExecutablePath -ieq $configuration.node -and $_.CommandLine -match 'Aetherfront-Control[\\/]managed[\\/]releases[\\/]control-[1-9][0-9]{0,8}-[a-f0-9]{64}[\\/](ControlPlane[\\/](broker-host|worker|observation-publisher)\.mjs|APFDiscordBot[\\/]src[\\/]index\.js)'})
    foreach($process in $processes){
        if($process.ProcessId -in (@($before.game)+@($before.pack))){throw 'Invalid control stop target'}
        $current=Get-CimInstance Win32_Process -Filter "ProcessId=$($process.ProcessId)" -ErrorAction SilentlyContinue
        if($current -and $current.CreationDate -eq $process.CreationDate -and $current.CommandLine -ceq $process.CommandLine){Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop}
    }
    Restore-RepairBootstrap (Join-Path $root 'bootstrap') $backup $rows
    if(($before|ConvertTo-Json -Compress) -cne ((Game-Identity)|ConvertTo-Json -Compress)){throw 'Game or pack process identity changed'}
    foreach($task in $tasks){$task.Enabled=$true}
    $lease.Dispose();$lease=$null
    $null=($tasks|Where-Object Name -CEQ 'Aetherfront Native Publisher').Run($null)
    Report 'running-reviewed-noninteractive-installer'
    & $configuration.powershell -NoProfile -NonInteractive -File $installerPath -SourceCommit $SourceCommit
    if($LASTEXITCODE -ne 0){throw 'Reviewed control installer did not complete'}
    $result.completed=$true
    Report 'completed'
}catch{
    $result.reason='CONTROL_RESUME_FAILED';$result.errorLine=$_.InvocationInfo.ScriptLineNumber
    [IO.File]::WriteAllText((Join-Path $root 'native-cache/control-resume-error.json'),($_|Out-String))
    Report $result.stage
}finally{if($lease){$lease.Dispose()}}
if(-not $result.completed){exit 1}
