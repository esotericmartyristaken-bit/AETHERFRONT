#requires -Version 7.5
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Archive,
    [Parameter(Mandatory)][ValidatePattern('^kesas-rc[1-9][0-9]{0,8}$')][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$SourceCommit,
    [ValidateSet('publish','verify')][string]$Action='publish',
    [switch]$Approve,[switch]$NoStart)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if(-not $Approve){throw 'Explicit -Approve is required; no request was queued'}
$config=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'operator.json') -Raw | ConvertFrom-Json
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if($identity.User.Value -cne $config.writerSid -or [Environment]::MachineName.ToLowerInvariant() -cne $config.host){throw 'Run approval as the configured VPS operator'}
if([Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0){throw 'Service-session processes cannot issue operator approvals'}
Add-Type -Path (Join-Path $PSScriptRoot 'SessionAccess.dll')
$null=[AetherfrontPublisherSessionAccess]::CurrentLogonSid()
function Start-PublisherTask {
    $scheduler=New-Object -ComObject Schedule.Service;$scheduler.Connect()
    $null=$scheduler.GetFolder('\').GetTask($config.taskName).Run($null)
}
$archivePath=(Resolve-Path -LiteralPath $Archive).Path
$staging=[IO.Path]::GetFullPath($config.stagingRoot).TrimEnd('\')+'\'
if(-not $archivePath.StartsWith($staging,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetExtension($archivePath) -cne '.zip'){throw 'Select a staged ZIP under the approved UpdateBuilds directory'}
for($item=Get-Item -LiteralPath $archivePath -Force;$item;$item=if($item -is [IO.FileInfo]){$item.Directory}else{$item.Parent}){
    if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Staged archive path contains a reparse point'}
}
if((Get-FileHash -LiteralPath $config.nodePath).Hash.ToLowerInvariant() -cne $config.nodeSha256){throw 'Approval runtime changed'}
$file=[IO.FileStream]::new($archivePath,'Open','Read','Read')
try {
    if($file.Length -le 0 -or $file.Length -ge 2GB){throw 'Staged archive exceeds policy'}
    $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($file)).ToLowerInvariant()
    foreach($name in @('pending.json','pending.sig')){if([IO.File]::Exists((Join-Path $config.inbox $name))){throw 'A publisher request is already pending'}}
    $target=Join-Path $config.inbox ($hash+'.zip')
    if(-not [IO.File]::Exists($target)){[IO.File]::Copy($archivePath,$target,$false)}
    if((Get-FileHash -LiteralPath $target).Hash.ToLowerInvariant() -cne $hash){throw 'Inbox archive differs'}
    # Supply the current GitHub session as an RSA-encrypted, signed per-job
    # envelope. Expired cached machine credentials do not require another UAC.
    if((Get-FileHash -LiteralPath $config.githubCliPath).Hash.ToLowerInvariant() -cne $config.githubCliSha256){throw 'GitHub approval executable changed'}
    $tokenText=(& $config.githubCliPath auth token --hostname github.com) -join ''
    if($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($tokenText)){throw 'Sign in to GitHub as the VPS operator before approving a release'}
    $credential=[Text.Encoding]::UTF8.GetBytes((@{githubToken=$tokenText}|ConvertTo-Json -Compress))
    $tokenText=$null;$rsa=[Security.Cryptography.RSA]::Create()
    try{$rsa.ImportFromPem([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'credential-public.pem')));$encrypted=$rsa.Encrypt($credential,[Security.Cryptography.RSAEncryptionPadding]::OaepSHA256)}finally{$rsa.Dispose();[Array]::Clear($credential)}
    $credentialHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($encrypted)).ToLowerInvariant()
    $credentialPath=Join-Path $config.inbox ($credentialHash+'.credentials')
    if(-not [IO.File]::Exists($credentialPath)){[IO.File]::WriteAllBytes($credentialPath,$encrypted)}
    if((Get-FileHash -LiteralPath $credentialPath).Hash.ToLowerInvariant() -cne $credentialHash){throw 'Credential envelope differs'}
    Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
    try{$cipher=[IO.File]::ReadAllBytes($config.privateKeyPath)}catch [UnauthorizedAccessException]{
        # This only starts the fixed protected task. It refreshes access for
        # genuine Windows interactive logon SIDs; it never elevates this shell.
        Start-PublisherTask
        $timer=[Diagnostics.Stopwatch]::StartNew();$cipher=$null
        while(-not $cipher -and $timer.Elapsed.TotalSeconds -lt 30){Start-Sleep -Milliseconds 500;try{$cipher=[IO.File]::ReadAllBytes($config.privateKeyPath)}catch [UnauthorizedAccessException]{}}
        if(-not $cipher){throw 'Operator key is unavailable to this logon; inspect publisher heartbeat'}
    }
    $secret=[Security.Cryptography.ProtectedData]::Unprotect($cipher,[Text.Encoding]::ASCII.GetBytes('AETHERFRONT VPS APPROVAL KEY v3'),[Security.Cryptography.DataProtectionScope]::CurrentUser)
    $requestId=[Guid]::NewGuid().ToString('N');$now=[DateTimeOffset]::UtcNow
    $format="yyyy-MM-dd'T'HH:mm:ss.fff'Z'"
    $signArguments=@((Join-Path $PSScriptRoot 'Sign-LocalReleaseRequest.mjs'),$config.host,$requestId,$Action,$Version,$hash,$SourceCommit,$credentialHash,$now.ToString($format),$now.AddMinutes(10).ToString($format),$config.publicKeySha256)
    $priorOptions=$env:NODE_OPTIONS;$priorPath=$env:NODE_PATH
    try {
        $env:NODE_OPTIONS=$null;$env:NODE_PATH=$null
        $signedText=([Convert]::ToBase64String($secret)|& $config.nodePath @signArguments) -join ''
        if($LASTEXITCODE -ne 0){throw 'Approval signing failed'}
        $signed=$signedText|ConvertFrom-Json
    } finally {[Array]::Clear($secret);$env:NODE_OPTIONS=$priorOptions;$env:NODE_PATH=$priorPath}
    $signature=[Convert]::FromBase64String($signed.signature);$bytes=[Convert]::FromBase64String($signed.request)
    if($signature.Length -ne 64 -or $bytes.Length -gt 1024){throw 'Invalid signed approval size'}
    $temporary=Join-Path $config.inbox ($requestId+'.tmp')
    [IO.File]::WriteAllBytes($temporary,$signature);[IO.File]::Move($temporary,(Join-Path $config.inbox 'pending.sig'))
    [IO.File]::WriteAllBytes($temporary,$bytes);[IO.File]::Move($temporary,(Join-Path $config.inbox 'pending.json'))
    if(-not $NoStart){Start-PublisherTask}
    [pscustomobject]@{requestId=$requestId;action=$Action;version=$Version;sourceCommit=$SourceCommit;archiveSha256=$hash;statusPath=(Join-Path $config.statusRoot ($requestId+'.json'));uacRequired=$false}
} finally {$file.Dispose()}
