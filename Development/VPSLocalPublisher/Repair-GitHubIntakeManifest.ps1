#requires -Version 7.5
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if([Environment]::MachineName -ine 'VMI3254434' -or
    -not [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
    throw 'Run from the elevated KESAS VPS operator session'
}

$root='C:\ProgramData\Aetherfront-VPSLocalPublisher'
$runner=Join-Path $root 'Runner-v3'
$backup=Join-Path $root 'Before-GitHub-d7d2a381432048f591d971d3b4bb27d3'
$priorHash='a4603bf4c2439cbf0ebd5c68870d2484866679b2ea83410ac11648af97c03844'
$installedHash='9ba6beebb1f115c9cd02ec2663df1edd286db84468ba4cd6e869df88211247ed'
$keyHash='f496103fb14b97778df12508e05fec8450c71be94135968182a0f15afd57aa15'
. (Join-Path $runner 'Publisher.Common.ps1')

function Get-Sha256([string]$Path){
    Assert-PublisherPath $Path $root
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

$lock=[IO.FileStream]::new((Join-Path $root 'instance-v3.lock'),'OpenOrCreate','ReadWrite','None')
try {
    $path=Join-Path $runner 'installed-files.json'
    $configPath=Join-Path $runner 'runner.json'
    Assert-PublisherPath $path $root
    if((Get-Sha256 (Join-Path $backup 'runner.json')) -cne $priorHash -or
        (Get-Sha256 $configPath) -cne $installedHash){throw 'Configuration differs from the audited repair case'}

    $config=Read-PublisherJson $configPath
    if($config.schemaVersion -ne 3 -or $config.host -cne 'vmi3254434' -or
        (Get-Sha256 $config.githubIntake.publicKeyPath) -cne $keyHash){throw 'GitHub intake identity differs'}
    $manifest=Read-PublisherJson $path
    $entries=@($manifest.files | Where-Object name -CEQ 'runner.json')
    if(@($manifest.files).Count -ne 16 -or $entries.Count -ne 1 -or
        $entries[0].sha256 -cnotin @($priorHash,$installedHash)){throw 'Manifest differs from the audited repair case'}
    foreach($row in $manifest.files){
        if($row.name -ceq 'runner.json'){continue}
        if($row.name -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9.-]*\z' -or
            (Get-Sha256 (Join-Path $runner $row.name)) -cne $row.sha256){throw 'Another runner manifest entry differs'}
    }
    if($entries[0].sha256 -ceq $installedHash){
        [pscustomobject]@{status='already-repaired';runnerSha256=$installedHash}
        return
    }
    $previous=Join-Path $backup ('manifest-before-repair-'+[Guid]::NewGuid().ToString('N')+'.json')
    [IO.File]::Copy($path,$previous,$false)
    Set-PublisherFile $previous
    $entries[0].sha256=$installedHash
    $temporary=Join-Path $runner ('manifest-repair-'+[Guid]::NewGuid().ToString('N')+'.tmp')
    try{
        Write-PublisherJson $temporary $manifest
        Set-PublisherFile $temporary
        [IO.File]::Move($temporary,$path,$true)
    }finally{[IO.File]::Delete($temporary)}
    [pscustomobject]@{status='repaired';runnerSha256=$installedHash;manifestBackup=$previous}
} finally {$lock.Dispose()}
