#requires -Version 7.5
[CmdletBinding()]
param([Parameter(Mandatory)][string]$NativeArchive,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$ExpectedNativeHash,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$ReleaseCommit,
    [Parameter(Mandatory)][ValidatePattern('^S-1-5-21-[0-9-]+$')][string]$ExpectedOperatorSid,
    [ValidatePattern('^kesas-rc[1-9][0-9]{0,8}$')][string]$Version='kesas-rc54',
    [switch]$PlanOnly)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Publisher.Common.ps1')
$root='C:\ProgramData\Aetherfront-VPSLocalPublisher'
$runner=Join-Path $root 'Runner-v3';$tools=Join-Path $root 'Tools-v3'
$approval=Join-Path $root 'Approval-v3';$secrets=Join-Path $root 'Secrets-v3'
$statusRoot=Join-Path $root 'Status-v3';$inbox=Join-Path $root 'Inbox-v3'
$taskName='Aetherfront VPS Local Publisher'
$control='C:\ProgramData\Aetherfront-Control'
$hostConfiguration=Join-Path $control 'host.json'
$settings=Read-PublisherJson $hostConfiguration
$live=[IO.Path]::GetFullPath($settings.nativeRoot);$bot=[IO.Path]::GetFullPath($settings.botRoot)
if([Environment]::MachineName -ine 'VMI3254434' -or $live -ine 'C:\Users\Administrator\Desktop\APF\KESAS-VPS-Server' -or $bot -ine 'C:\Users\Administrator\Desktop\APF\APFDiscordBot'){throw 'Unexpected production host or roots'}
$powerShell='C:\Program Files\PowerShell\7\pwsh.exe'
$node='C:\Program Files\nodejs\node.exe';$git='C:\Program Files\Git\cmd\git.exe';$gh='C:\Program Files\GitHub CLI\gh.exe'
$inputs=@('Worker.mjs','LocalReleaseRequest.mjs','Invoke-VPSLocalPublisher.ps1','Publisher.Common.ps1','Read-JobCredential.ps1','Assert-LocalRelease.ps1','SessionAccess.cs','Approve-LocalRelease.ps1','Sign-LocalReleaseRequest.mjs','Get-PublisherStatus.ps1')
foreach($name in $inputs){if(-not [IO.File]::Exists((Join-Path $PSScriptRoot $name))){throw "Missing reviewed setup source: $name"}}
$verifier=Join-Path $PSScriptRoot 'Verify-KesasRelease.ps1'
if(-not [IO.File]::Exists($verifier)){$verifier=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../APFDiscordBot/scripts/Verify-KesasRelease.ps1'))}
if((Get-FileHash -LiteralPath $NativeArchive).Hash.ToLowerInvariant() -cne $ExpectedNativeHash){throw 'Initial release archive differs'}
& $verifier -Archive $NativeArchive -ExpectedVersion $Version | Out-Null
$active=Read-PublisherJson (Join-Path $live 'Updates/APFClientUpdate.json')
if($active.version -notmatch '^kesas-rc[1-9][0-9]*$' -or [IO.File]::Exists((Join-Path $live '.update-transaction.json'))){throw 'An unresolved native activation requires review before commissioning'}
foreach($exe in @($powerShell,$node,$git,$gh)){if(-not [IO.File]::Exists($exe)){throw 'Required installed runtime is missing'}}
$plan=[ordered]@{host='vmi3254434';operatorSid=$ExpectedOperatorSid;activeVersion=$active.version;releaseVersion=$Version;releaseCommit=$ReleaseCommit;archiveSha256=$ExpectedNativeHash;task=$taskName;taskIdentity='SYSTEM';root=$root;approval='interactive logon SID plus CurrentUser DPAPI';networkListenerAdded=$false;setupGameRestart=$false;files=@($inputs|ForEach-Object{@{name=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $_)).Hash.ToLowerInvariant()}})}
if($PlanOnly){$plan|ConvertTo-Json -Depth 6;return}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if($identity.User.Value -cne $ExpectedOperatorSid -or -not [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Use the one approved VPS operator elevation for commissioning'}
if([IO.File]::Exists((Join-Path $runner 'installed-files.json'))){throw 'Version 3 is already installed; inspect its receipt instead of overwriting a running generation'}
Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
$gameBefore=@(Get-NetUDPEndpoint -LocalPort 18865 -ErrorAction Stop|Select-Object -ExpandProperty OwningProcess -Unique)
if($gameBefore.Count -ne 1){throw 'A unique live game listener is required during setup'}
$scheduler=New-Object -ComObject Schedule.Service;$scheduler.Connect();$folder=$scheduler.GetFolder('\')
$gameTask=$folder.GetTask('KESAS Server')
if($gameTask.Definition.Principal.UserId -notin @('SYSTEM','NT AUTHORITY\SYSTEM','S-1-5-18')){throw 'Existing protected SYSTEM game task is required'}
$botTask=$folder.GetTask('APF Discord Bot')
if($botTask.Definition.Principal.LogonType -ne 2 -or $botTask.Definition.Principal.RunLevel -ne 0){throw 'Bot must retain its limited S4U service token'}
foreach($name in @($taskName,'Aetherfront VPS Local Publisher Smoke','Aetherfront Protected Publisher','Aetherfront Protected Publisher Smoke','Aetherfront Native Publisher')){
    $old=$null;try{$old=$folder.GetTask($name)}catch [Runtime.InteropServices.COMException]{}
    if($old){$old.Enabled=$false;if($old.State -eq 4){throw 'An earlier privileged publisher is still running; wait for its normal completion'}}
}
Set-PublisherDirectory $root @($ExpectedOperatorSid) -RootOnlyReaders
foreach($directory in @($runner,$approval,$secrets,(Join-Path $root 'Jobs-v3'),(Join-Path $root 'Claims-v3'),(Join-Path $root 'Rejected-v3'),(Join-Path $root 'Sources-v3'))){Set-PublisherDirectory $directory}
Set-PublisherDirectory $tools @($ExpectedOperatorSid)
Set-PublisherDirectory $statusRoot @($ExpectedOperatorSid)
Set-PublisherDirectory $inbox @() $ExpectedOperatorSid
foreach($name in $inputs){[IO.File]::Copy((Join-Path $PSScriptRoot $name),(Join-Path $runner $name),$false)}
[IO.File]::Copy($verifier,(Join-Path $runner 'Verify-KesasRelease.ps1'),$false)
Add-Type -Path (Join-Path $runner 'SessionAccess.cs') -OutputAssembly (Join-Path $runner 'SessionAccess.dll')

# Fresh key: the old shared-user approval key is retained as historical evidence
# but is never accepted by this worker. It never leaves protected storage.
$priorOptions=$env:NODE_OPTIONS;$priorNodePath=$env:NODE_PATH
try {
    $env:NODE_OPTIONS=$null;$env:NODE_PATH=$null
    $keyText=(& $node -e 'const c=require("node:crypto");const k=c.generateKeyPairSync("ed25519");console.log(JSON.stringify({private:k.privateKey.export({format:"der",type:"pkcs8"}).toString("base64"),public:k.publicKey.export({format:"pem",type:"spki"}),hash:c.createHash("sha256").update(k.publicKey.export({format:"der",type:"spki"})).digest("hex")}));') -join ''
    if($LASTEXITCODE -ne 0){throw 'Approval key generation failed'}
    $key=$keyText|ConvertFrom-Json;$private=[Convert]::FromBase64String($key.private)
    try{$cipher=[Security.Cryptography.ProtectedData]::Protect($private,[Text.Encoding]::ASCII.GetBytes('AETHERFRONT VPS APPROVAL KEY v3'),[Security.Cryptography.DataProtectionScope]::CurrentUser)}finally{[Array]::Clear($private)}
    $privateKeyPath=Join-Path $approval 'approval-key.dpapi';[IO.File]::WriteAllBytes($privateKeyPath,$cipher)
    [IO.File]::WriteAllText((Join-Path $runner 'approval-public.pem'),$key.public,[Text.Encoding]::ASCII)
    $publicKeyHash=$key.hash;$key=$null;$keyText=$null
} finally {$env:NODE_OPTIONS=$priorOptions;$env:NODE_PATH=$priorNodePath}
$rsa=[Security.Cryptography.RSA]::Create(4096)
try {
    $private=$rsa.ExportPkcs8PrivateKey()
    try{$rsaCipher=[Security.Cryptography.ProtectedData]::Protect($private,[Text.Encoding]::ASCII.GetBytes('AETHERFRONT JOB CREDENTIAL RSA v3'),[Security.Cryptography.DataProtectionScope]::LocalMachine)}finally{[Array]::Clear($private)}
    $credentialKey=Join-Path $secrets 'credential-key.dpapi';[IO.File]::WriteAllBytes($credentialKey,$rsaCipher)
    [IO.File]::WriteAllText((Join-Path $tools 'credential-public.pem'),$rsa.ExportSubjectPublicKeyInfoPem(),[Text.Encoding]::ASCII)
} finally {$rsa.Dispose()}
$github=(& $gh auth token --hostname github.com) -join ''
if($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($github)){throw 'The VPS GitHub login must be working before commissioning'}
Protect-PublisherSecret (Join-Path $secrets 'github.dpapi') 'github' $github;$github=$null
$botCipher=[IO.File]::ReadAllBytes((Join-Path $control 'bootstrap/bot-credential.dpapi'))
$plain=[Security.Cryptography.ProtectedData]::Unprotect($botCipher,$null,[Security.Cryptography.DataProtectionScope]::LocalMachine)
try{Protect-PublisherSecret (Join-Path $secrets 'discord.dpapi') 'discord' ([Text.Encoding]::UTF8.GetString($plain))}finally{[Array]::Clear($plain)}

# Seal executable inputs without touching universe, logs, identities, preferences
# or any file bytes. Directory names are pinned during each later deployment.
$aclBackup=[Collections.Generic.List[object]]::new()
function Protect-ExecutionTree([string]$Directory) {
    $item=Get-Item -LiteralPath $Directory -Force
    if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Execution directory contains a reparse point'}
    $aclBackup.Add(@{path=$Directory;sddl=(Get-Acl -LiteralPath $Directory).Sddl})
    Set-PublisherDirectory $Directory @($ExpectedOperatorSid) -RootOnlyReaders
    foreach($child in Get-ChildItem -LiteralPath $Directory -Force){
        if($child.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Execution tree contains a reparse point'}
        if($child.PSIsContainer){
            if($child.Name -notin @('Universe','Aetherfront','Logs','APFDiscordBridge','secrets','Identity','Backups','Cache','node_modules')){Protect-ExecutionTree $child.FullName}
        } elseif($child.Extension -in @('.dll','.exe','.ps1','.mjs','.js','.json','.config','.xml','.pdb')){
            $aclBackup.Add(@{path=$child.FullName;sddl=(Get-Acl -LiteralPath $child.FullName).Sddl})
            $readers=if($child.Name -match '(?i)private|secret|credential|password'){@()}else{@($ExpectedOperatorSid)}
            Set-PublisherFile $child.FullName $readers
        }
    }
}
foreach($directory in @($live,$bot)){
    $aclBackup.Add(@{path=$directory;sddl=(Get-Acl -LiteralPath $directory).Sddl})
    Set-PublisherDirectory $directory @($ExpectedOperatorSid) -RootOnlyReaders
}
$executionRoots=@('Server','PackHost','CertificateTool','UpdateTool')|ForEach-Object{Join-Path $live $_}
foreach($directory in $executionRoots){Protect-ExecutionTree $directory}
$configPaths=@($hostConfiguration,(Join-Path $bot 'config.json'),(Join-Path $live 'deployment.json'))
foreach($path in $configPaths){$aclBackup.Add(@{path=$path;sddl=(Get-Acl -LiteralPath $path).Sddl});Set-PublisherFile $path @($ExpectedOperatorSid)}
Write-PublisherJson (Join-Path $root 'acl-before-v3.json') $aclBackup
$config=[ordered]@{
    schemaVersion=3;host='vmi3254434';root=$root;writerSid=$ExpectedOperatorSid
    inbox=$inbox;statusRoot=$statusRoot;approvalRoot=$approval
    privateJobs=(Join-Path $root 'Jobs-v3');claims=(Join-Path $root 'Claims-v3');rejected=(Join-Path $root 'Rejected-v3');repoRoot=(Join-Path $root 'Sources-v3')
    publicKeyPath=(Join-Path $runner 'approval-public.pem');credentialPrivateKeyPath=$credentialKey
    releaseVerifier=(Join-Path $runner 'Assert-LocalRelease.ps1');credentialDecryptor=(Join-Path $runner 'Read-JobCredential.ps1')
    activeFeedPath=(Join-Path $live 'Updates/APFClientUpdate.json');nativeEvidenceRoot='C:\ProgramData\Aetherfront-Local\releases'
    powerShell=$powerShell;powerShellSha256=(Get-FileHash $powerShell).Hash.ToLowerInvariant()
    nodePath=$node;nodeSha256=(Get-FileHash $node).Hash.ToLowerInvariant()
    gitPath=$git;gitSha256=(Get-FileHash $git).Hash.ToLowerInvariant()
    githubCliPath=$gh;githubCliSha256=(Get-FileHash $gh).Hash.ToLowerInvariant()
    githubTokenPath=(Join-Path $secrets 'github.dpapi');tokenPath=(Join-Path $secrets 'discord.dpapi')
    moduleSearch=@("$env:SystemRoot\System32\WindowsPowerShell\v1.0\Modules",(Join-Path (Split-Path $powerShell) 'Modules'))
    pinnedDirectories=@($live,$bot,$control)+@($executionRoots)
    executionRoots=@($executionRoots)
    configurationPins=@($configPaths|ForEach-Object{@{path=$_;sha256=(Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant()}})
}
Write-PublisherJson (Join-Path $runner 'runner.json') $config
foreach($name in @('Approve-LocalRelease.ps1','Sign-LocalReleaseRequest.mjs','LocalReleaseRequest.mjs','Get-PublisherStatus.ps1')){[IO.File]::Copy((Join-Path $runner $name),(Join-Path $tools $name),$false)}
[IO.File]::Copy((Join-Path $runner 'SessionAccess.dll'),(Join-Path $tools 'SessionAccess.dll'),$false)
$operator=[ordered]@{host='vmi3254434';writerSid=$ExpectedOperatorSid;taskName=$taskName;inbox=$inbox;statusRoot=$statusRoot;privateKeyPath=$privateKeyPath;publicKeySha256=$publicKeyHash;nodePath=$node;nodeSha256=$config.nodeSha256;githubCliPath=$gh;githubCliSha256=$config.githubCliSha256;stagingRoot='C:\Users\Administrator\Desktop\APF\UpdateBuilds'}
Write-PublisherJson (Join-Path $tools 'operator.json') $operator
$initialLogons=@([AetherfrontPublisherSessionAccess]::CurrentLogonSid())
Set-PublisherDirectory $approval $initialLogons
Set-PublisherFile $privateKeyPath $initialLogons
$manifest=@{files=@(Get-ChildItem -LiteralPath $runner -File|Where-Object Name -Ne 'installed-files.json'|ForEach-Object{@{name=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}})}
Write-PublisherJson (Join-Path $runner 'installed-files.json') $manifest
$entry=Join-Path $runner 'Invoke-VPSLocalPublisher.ps1'
$action=New-ScheduledTaskAction -Execute $powerShell -Argument "-NoLogo -NoProfile -NonInteractive -File `"$entry`"" -WorkingDirectory $runner
$principal=New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$options=New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
$startup=New-ScheduledTaskTrigger -AtStartup
$periodic=New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 1)
$definition=New-ScheduledTask -Action $action -Principal $principal -Settings $options -Trigger $startup,$periodic
Register-ScheduledTask -TaskName $taskName -TaskPath '\' -InputObject $definition -Force | Out-Null
$task=$folder.GetTask($taskName)
$task.SetSecurityDescriptor("D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGX;;;$ExpectedOperatorSid)",0)
if($task.Definition.Principal.UserId -notin @('SYSTEM','S-1-5-18','NT AUTHORITY\SYSTEM') -or $task.Definition.Settings.ExecutionTimeLimit -ne 'PT0S'){throw 'Persistent task identity/lifetime differs'}
$queued=& (Join-Path $tools 'Approve-LocalRelease.ps1') -Archive $NativeArchive -Version $Version -SourceCommit $ReleaseCommit -Action verify -Approve
$deadline=[DateTime]::UtcNow.AddMinutes(8);$receipt=$null
do {
    Start-Sleep -Seconds 1
    if([IO.File]::Exists($queued.statusPath)){$receipt=Read-PublisherJson $queued.statusPath;if($receipt.status -in @('verified','failed','rejected')){break}}
} while([DateTime]::UtcNow -lt $deadline)
if(-not $receipt -or $receipt.status -cne 'verified' -or $receipt.systemIdentity -ne $true){throw 'SYSTEM verification did not complete; inspect public status and preserved evidence'}
$currentGame=@(Get-NetUDPEndpoint -LocalPort 18865|Select-Object -ExpandProperty OwningProcess -Unique)
if($currentGame.Count -ne 1 -or $currentGame[0] -ne $gameBefore[0]){throw 'Game process changed during setup'}
$result=@{status='healthy';task=$taskName;systemVerification=$receipt;setupGameRestart=$false;serverPid=$gameBefore[0];operatorTools=$tools;root=$root;sourceCommit=$ReleaseCommit;installedAtUtc=[DateTime]::UtcNow.ToString('o')}
Write-PublisherJson (Join-Path $statusRoot 'install-result.json') $result
$result|ConvertTo-Json -Depth 7
