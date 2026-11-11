#requires -Version 7.5
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ControllerPublicKey,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$ExpectedPublicKeySha256
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if([Environment]::MachineName -ine 'VMI3254434' -or
    -not [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
    throw 'Install from the elevated operator session on the KESAS VPS'
}
$root='C:\ProgramData\Aetherfront-VPSLocalPublisher'
$runner=Join-Path $root 'Runner-v3'
. (Join-Path $runner 'Publisher.Common.ps1')
Assert-PublisherPath $runner $root
$configPath=Join-Path $runner 'runner.json'
$manifestPath=Join-Path $runner 'installed-files.json'
$publicKeyBytes=[IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $ControllerPublicKey).Path)
$keyHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($publicKeyBytes)).ToLowerInvariant()
if($keyHash -cne $ExpectedPublicKeySha256 -or $publicKeyBytes.Length -gt 512 -or
    [Text.Encoding]::ASCII.GetString($publicKeyBytes) -cnotmatch '\A-----BEGIN PUBLIC KEY-----\r?\n[A-Za-z0-9+/=]+\r?\n-----END PUBLIC KEY-----\r?\n?\z'){
    throw 'Controller public key differs from the reviewed key'
}

$lock=[IO.FileStream]::new((Join-Path $root 'instance-v3.lock'),'OpenOrCreate','ReadWrite','None')
try {
    Assert-PublisherPath $configPath $root
    Assert-PublisherPath $manifestPath $root
    $config=Read-PublisherJson $configPath
    if($config.schemaVersion -ne 3 -or $config.host -cne 'vmi3254434' -or $config.PSObject.Properties['githubIntake']){
        throw 'Expected the installed VPS-local v3 publisher before GitHub intake'
    }
    if([IO.File]::Exists((Join-Path $config.inbox 'pending.json'))){throw 'A local publisher job is waiting'}
    $manifest=Read-PublisherJson $manifestPath
    foreach($row in $manifest.files){
        $file=Join-Path $runner $row.name
        Assert-PublisherPath $file $root
        if((Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant() -cne $row.sha256){
            throw 'Installed runner differs from its manifest'
        }
    }
    $prior=@('Invoke-VPSLocalPublisher.ps1','Worker.mjs')
    $additional=@('GitHubReleaseInbox.mjs','GitHubReleaseWorker.mjs')
    foreach($name in $additional){
        if(Test-Path -LiteralPath (Join-Path $runner $name)){throw 'GitHub intake code already exists'}
    }
    $operation=[Guid]::NewGuid().ToString('N')
    $backup=Join-Path $root "Before-GitHub-$operation"
    $private=Join-Path $root "GitHub-v3-$operation"
    Set-PublisherDirectory $backup
    Set-PublisherDirectory $private
    foreach($dir in @('Inboxes','Attempts','Outbox')){Set-PublisherDirectory (Join-Path $private $dir)}
    $publicKey=Join-Path $private 'controller-public.pem'
    [IO.File]::WriteAllBytes($publicKey,$publicKeyBytes)
    Set-PublisherFile $publicKey
    foreach($name in $prior+@('runner.json','installed-files.json')){
        [IO.File]::Copy((Join-Path $runner $name),(Join-Path $backup $name),$false)
    }
    # Reviewed publisher and bot source trees at 7f466ddb93aee768e854bde53632be5ca88ca384.
    $trees=[ordered]@{
        'APFDiscordBot'='69ab7350400a6eacb0156f3fc11da8c1dc258675'
        'Aetherfront-RC44-Work/Deployment'='91449318933a1ca59a43a4867c6805ffd9715897'
        'ControlPlane'='68d6b6f579965ef610fa78238c6ead095ecf885a'
        'KESAS-VPS-Server'='80f021a098924ed9912c7bee683467d30a99c2e0'
    }
    $config | Add-Member -NotePropertyName githubIntake -NotePropertyValue @{
        publicKeyPath=$publicKey;inboxes=(Join-Path $private 'Inboxes');attempts=(Join-Path $private 'Attempts')
        outbox=(Join-Path $private 'Outbox');sourceTrees=$trees
    }
    function Install-File([string]$From,[string]$To){
        $temporary=$To+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
        [IO.File]::Copy($From,$temporary,$false)
        Set-PublisherFile $temporary
        [IO.File]::Move($temporary,$To,$true)
    }
    try {
        $names=$prior+$additional
        $rows=@($manifest.files | Where-Object name -CNotIn ($names+@('runner.json')))
        foreach($name in $names){
            $destination=Join-Path $runner $name
            Install-File (Join-Path $PSScriptRoot $name) $destination
            $rows+=@{name=$name;sha256=(Get-FileHash -LiteralPath $destination).Hash.ToLowerInvariant()}
        }
        Write-PublisherJson $configPath $config
        Set-PublisherFile $configPath
        $rows+=@{name='runner.json';sha256=(Get-FileHash -LiteralPath $configPath).Hash.ToLowerInvariant()}
        foreach($row in $rows){
            if((Get-FileHash -LiteralPath (Join-Path $runner $row.name)).Hash.ToLowerInvariant() -cne $row.sha256){
                throw 'Runner manifest does not match installed files'
            }
        }
        Write-PublisherJson $manifestPath @{files=$rows}
        Set-PublisherFile $manifestPath
    } catch {
        foreach($name in $prior+@('runner.json','installed-files.json')){
            Install-File (Join-Path $backup $name) (Join-Path $runner $name)
        }
        foreach($name in $additional){[IO.File]::Delete((Join-Path $runner $name))}
        throw
    }
    [pscustomobject]@{status='installed-awaiting-verify-request';controllerPublicKeySha256=$keyHash;rollback=$backup}
} finally {$lock.Dispose()}
