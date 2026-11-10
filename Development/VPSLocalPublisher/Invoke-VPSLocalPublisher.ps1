#requires -Version 7.5
param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root='C:\ProgramData\Aetherfront-VPSLocalPublisher'
if([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne 'S-1-5-18'){throw 'Persistent publisher requires its fixed SYSTEM task'}
if($PSScriptRoot -ine (Join-Path $root 'Runner-v3')){throw 'Unexpected persistent publisher location'}
. (Join-Path $PSScriptRoot 'Publisher.Common.ps1')
Assert-PublisherPath $PSScriptRoot $root
$config=$null;$lock=$null;$pins=$null;$streams=[Collections.Generic.List[IO.FileStream]]::new()
try {
    try{$lock=[IO.FileStream]::new((Join-Path $root 'instance-v3.lock'),'OpenOrCreate','ReadWrite','None')}catch [IO.IOException]{exit 0}
    # File ownership, code hashes and protected configuration are checked before
    # interpreting requests or loading credentials.
    $configPath=Join-Path $PSScriptRoot 'runner.json';Assert-PublisherPath $configPath $root
    $config=Read-PublisherJson $configPath
    $manifest=Read-PublisherJson (Join-Path $PSScriptRoot 'installed-files.json')
    foreach($row in $manifest.files){
        $path=Join-Path $PSScriptRoot $row.name
        Assert-PublisherPath $path $root
        if((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -cne $row.sha256){throw 'Installed publisher code differs from its reviewed manifest'}
    }
    Add-Type -Path (Join-Path $PSScriptRoot 'SessionAccess.dll')
    $sessions=Set-PublisherInteractiveKeyAccess $config
    Write-PublisherJson (Join-Path $config.statusRoot 'heartbeat.json') @{schemaVersion=3;userSid='S-1-5-18';ready=$true;interactiveOperatorSessions=$sessions;observedAtUtc=[DateTime]::UtcNow.ToString('o')}
    if(-not [IO.File]::Exists((Join-Path $config.inbox 'pending.json'))){exit 0}
    foreach($pair in @(@('nodePath','nodeSha256'),@('gitPath','gitSha256'),@('powerShell','powerShellSha256'),@('githubCliPath','githubCliSha256'))){
        if((Get-FileHash -LiteralPath $config.($pair[0])).Hash.ToLowerInvariant() -cne $config.($pair[1])){throw 'Approved publisher executable changed'}
    }
    $directories=@($config.pinnedDirectories)
    $pins=[AetherfrontPublisherSessionAccess]::PinAncestors($directories)
    foreach($path in @($config.pinnedDirectories)){Assert-PublisherPath $path $path}
    foreach($row in $config.configurationPins){
        Assert-PublisherPath $row.path ([IO.Path]::GetDirectoryName($row.path))
        $stream=[IO.FileStream]::new($row.path,'Open','Read','Read');$streams.Add($stream)
        if([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() -cne $row.sha256){throw 'Publisher execution configuration changed; signed maintenance is required'}
    }
    foreach($path in @($config.publicKeyPath,$config.githubTokenPath,$config.tokenPath,$config.privateJobs,$config.claims,$config.repoRoot)) {Assert-PublisherPath $path $root}
    Assert-PublisherPath $config.inbox $root -WritableInbox
    Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
    foreach($name in @('NODE_OPTIONS','NODE_PATH','NODE_EXTRA_CA_CERTS','NODE_TLS_REJECT_UNAUTHORIZED','HTTPS_PROXY','HTTP_PROXY','ALL_PROXY','GITHUB_TOKEN')){[Environment]::SetEnvironmentVariable($name,$null,'Process')}
    try{$env:GH_TOKEN=Read-PublisherSecret $config.githubTokenPath 'github'}catch{$env:GH_TOKEN=$null}
    try{$env:APF_DISCORD_TOKEN=Read-PublisherSecret $config.tokenPath 'discord'}catch{$env:APF_DISCORD_TOKEN=$null}
    $env:APF_PUBLISHER_SYSTEM_VERIFIED='1'
    $env:PATH=(@((Split-Path $config.powerShell),(Split-Path $config.nodePath),(Split-Path $config.gitPath),(Split-Path $config.githubCliPath),"$env:SystemRoot\System32") -join ';')
    & $config.nodePath (Join-Path $PSScriptRoot 'Worker.mjs')
    if($LASTEXITCODE -ne 0){throw 'Persistent worker failed; inspect sanitized status'}
} catch {
    if($config -and $config.statusRoot){Write-PublisherJson (Join-Path $config.statusRoot 'runner-error.json') @{status='failed';stage='runner';code=$_.Exception.GetType().Name;observedAtUtc=[DateTime]::UtcNow.ToString('o')}}
    throw
} finally {
    $env:GH_TOKEN=$null;$env:APF_DISCORD_TOKEN=$null;$env:APF_PUBLISHER_SYSTEM_VERIFIED=$null
    foreach($stream in $streams){$stream.Dispose()}
    if($pins){$pins.Dispose()};if($lock){$lock.Dispose()}
}
